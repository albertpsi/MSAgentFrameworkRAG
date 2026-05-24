using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenAI.Embeddings;
using Pinecone;
using MSAgentFrameworkRAG.Interfaces;

namespace MSAgentFrameworkRAG.Services
{
    public class RetrievalService : IRetrievalService
    {
        private readonly OpenAISettings _openAiSettings;
        private readonly PineconeSettings _pineconeSettings;
        private readonly IRerankService _rerankService;
        private readonly ILogger<RetrievalService> _logger;

        public RetrievalService(
            IOptions<OpenAISettings> openAiOptions,
            IOptions<PineconeSettings> pineconeOptions,
            IRerankService rerankService,
            ILogger<RetrievalService> logger)
        {
            _openAiSettings = openAiOptions?.Value ?? throw new ArgumentNullException(nameof(openAiOptions));
            _pineconeSettings = pineconeOptions?.Value ?? throw new ArgumentNullException(nameof(pineconeOptions));
            _rerankService = rerankService ?? throw new ArgumentNullException(nameof(rerankService));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public async Task<List<SourceCitation>> RetrieveContextAsync(string query, string? documentId = null)
        {
            // Parse list of document ids if comma-separated or dynamic
            List<string> documentIds = new();
            if (!string.IsNullOrEmpty(documentId))
            {
                documentIds = documentId.Split(',', StringSplitOptions.RemoveEmptyEntries)
                    .Select(id => id.Trim())
                    .ToList();
            }

            return await RetrieveContextAsync(query, documentIds).ConfigureAwait(false);
        }

        public async Task<List<SourceCitation>> RetrieveContextAsync(string query, List<string>? documentIds)
        {
            _logger.LogInformation("[Retrieval Service] Finding relevant vectors for query: '{Query}'...", query);
            var candidates = new List<SourceCitation>();

            try
            {
                // 1. Generate Embeddings for the search query
                var client = new EmbeddingClient(
                    model: _openAiSettings.EmbeddingModel ?? "text-embedding-3-small", 
                    apiKey: _openAiSettings.ApiKey);
                var embeddingResponse = await client.GenerateEmbeddingAsync(query).ConfigureAwait(false);
                var queryVector = embeddingResponse.Value.ToFloats().ToArray();

                // 2. Build Pinecone client & index
                var pinecone = new PineconeClient(_pineconeSettings.ApiKey);
                var index = pinecone.Index(_pineconeSettings.IndexName);

                // 3. Build Metadata Filters dynamically
                Metadata? filter = null;

                if (documentIds != null && documentIds.Count > 0)
                {
                    if (documentIds.Count == 1)
                    {
                        // Targeted single historical file search: bypass isLatest check to consult archives
                        filter = new Metadata { ["documentId"] = new MetadataValue(documentIds[0]) };
                    }
                    else
                    {
                        // Multi-file search across specific active files
                        var innerFilter = new Metadata();
                        innerFilter["$in"] = new MetadataValue(documentIds.Select(id => new MetadataValue(id)).ToArray());
                        filter = new Metadata { ["documentId"] = new MetadataValue(innerFilter) };
                        
                        // Restrict to latest versions within the active files
                        filter["isLatest"] = new MetadataValue("true");
                    }
                }
                else
                {
                    // Global search: strictly restrict to latest files
                    filter = new Metadata { ["isLatest"] = new MetadataValue("true") };
                }

                // 4. Perform vector query (Stage 1 - Coarse Search)
                // Use QueryTopK from settings (e.g. 40) rather than a static TopK of 10 to ensure a robust candidate pool
                var topK = _pineconeSettings.QueryTopK > 0 ? _pineconeSettings.QueryTopK : 40;
                _logger.LogInformation("[Retrieval Service] querying Pinecone index for TopK = {TopK} raw matches...", topK);

                var queryResponse = await index.QueryAsync(new QueryRequest
                {
                    Vector = new ReadOnlyMemory<float>(queryVector),
                    TopK = (uint)topK,
                    IncludeMetadata = true,
                    Filter = filter
                }).ConfigureAwait(false);

                var matches = (queryResponse.Matches ?? Enumerable.Empty<ScoredVector>()).ToList();
                _logger.LogInformation("[Retrieval Service] Found {Count} matching vectors in Pinecone.", matches.Count);

                foreach (var match in matches)
                {
                    var md = match.Metadata ?? new Metadata();
                    var content = md.TryGetValue("Content", out var contentVal) ? contentVal.ToString() : "";
                    if (string.IsNullOrEmpty(content))
                    {
                        content = md.TryGetValue("content", out var contentVal2) ? contentVal2.ToString() : "";
                    }

                    var sourceName = md.TryGetValue("sourceName", out var nameVal) ? nameVal.ToString() : "Pinecone search result";
                    var sourceLink = md.TryGetValue("sourceLink", out var linkVal) ? linkVal.ToString() : "";
                    
                    var chunkIndex = md.TryGetValue("chunkIndex", out var chunkVal) ? chunkVal.ToString() : "";
                    var pageNumber = md.TryGetValue("pageNumber", out var pageVal) ? pageVal.ToString() : "";

                    var citationName = sourceName;
                    if (!string.IsNullOrEmpty(pageNumber))
                    {
                        citationName += $" Page {pageNumber}";
                    }
                    else if (!string.IsNullOrEmpty(chunkIndex))
                    {
                        citationName += $" Chunk {chunkIndex}";
                    }

                    candidates.Add(new SourceCitation
                    {
                        SourceName = citationName,
                        SourceLink = sourceLink,
                        Text = content ?? "",
                        Score = match.Score // Store raw vector cosine similarity score for diagnostics
                    });
                }

                // 5. Invoke native Pinecone Reranking (Stage 2 - Fine Search)
                if (candidates.Any())
                {
                    var rerankedCitations = await _rerankService.RerankAsync(query, candidates, CancellationToken.None).ConfigureAwait(false);
                    return rerankedCitations;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[Retrieval Service Error] Two-stage retrieval failed.");
            }

            return candidates.Take(_pineconeSettings.RerankTopN).ToList();
        }
    }
}