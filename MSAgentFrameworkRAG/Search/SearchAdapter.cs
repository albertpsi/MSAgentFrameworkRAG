using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Agents.AI;
using OpenAI.Embeddings;
using Pinecone;
using MSAgentFrameworkRAG.Interfaces;

namespace MSAgentFrameworkRAG
{
    public sealed class PineconeTextSearchAdapter
    {
        private readonly PineconeClient _pinecone;
        private readonly string _indexName;
        private readonly string _openAIApiKey;
        private readonly string _embeddingModel;
        private readonly EmbeddingGenerationOptions? _embeddingOptions;
        private readonly Metadata? _filter;
        private readonly IRerankService _rerankService;
        private readonly AppDbContext _dbContext;
        private readonly int _topK;

        // Stores the exact reranked, score-filtered chunks used by the LLM
        // to be reused directly as citations in the UI without making a second search
        public List<SourceCitation> LastSearchResults { get; } = new();

        public PineconeTextSearchAdapter(
            PineconeClient pinecone,
            string indexName,
            string openAIApiKey,
            IRerankService rerankService,
            AppDbContext dbContext,
            EmbeddingGenerationOptions? embeddingOptions = null,
            Metadata? filter = null,
            string embeddingModel = "text-embedding-3-small",
            int topK = 40)
        {
            _pinecone = pinecone ?? throw new ArgumentNullException(nameof(pinecone));
            _indexName = string.IsNullOrWhiteSpace(indexName) ? throw new ArgumentException("Index name is required.", nameof(indexName)) : indexName;
            _openAIApiKey = string.IsNullOrWhiteSpace(openAIApiKey) ? throw new ArgumentException("OpenAI API key is required.", nameof(openAIApiKey)) : openAIApiKey;
            _rerankService = rerankService ?? throw new ArgumentNullException(nameof(rerankService));
            _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
            _embeddingOptions = embeddingOptions;
            _filter = filter;
            _embeddingModel = embeddingModel;
            _topK = topK;
        }

        public async Task<IEnumerable<TextSearchProvider.TextSearchResult>> SearchAsync(
            string query,
            CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(query))
            {
                return [];
            }

            // Extract raw Document IDs from the filter if available to align sparse search
            List<string>? filterDocIds = null;
            if (_filter != null && _filter.TryGetValue("documentId", out var docIdVal))
            {
                if (docIdVal != null)
                {
                    var valStr = docIdVal.ToString();
                    if (!string.IsNullOrEmpty(valStr))
                    {
                        if (valStr.Contains("["))
                        {
                            var matches = System.Text.RegularExpressions.Regex.Matches(valStr, @"""([^""]+)""");
                            filterDocIds = new List<string>();
                            foreach (System.Text.RegularExpressions.Match m in matches)
                            {
                                var id = m.Groups[1].Value;
                                if (id != "$in" && id != "documentId" && id != "isLatest" && id != "true")
                                {
                                    filterDocIds.Add(id);
                                }
                            }
                        }
                        else
                        {
                            if (!valStr.StartsWith("{"))
                            {
                                filterDocIds = new List<string> { valStr };
                            }
                        }
                    }
                }
            }

            // 1. Run Parallel Retrievals (Dense Vector & Sparse BM25 Keyword)
            var denseTask = GetDenseCandidatesAsync(query, cancellationToken);
            var sparseTask = LocalBm25Retriever.RetrieveBm25CandidatesAsync(_dbContext, query, filterDocIds, topN: 15);

            await Task.WhenAll(denseTask, sparseTask).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();

            var denseCandidates = await denseTask;
            var sparseCandidates = await sparseTask;

            // 2. Fusion and De-duplication of Candidates
            var candidatePool = new List<SourceCitation>(denseCandidates);
            foreach (var sparse in sparseCandidates)
            {
                if (!candidatePool.Any(c => c.Text.Equals(sparse.Text, StringComparison.Ordinal)))
                {
                    candidatePool.Add(sparse);
                }
            }

            if (candidatePool.Count == 0)
            {
                return [];
            }

            // 3. Fine Native Reranking & Score Filtering (Stage 2)
            var reranked = await _rerankService.RerankAsync(query, candidatePool, cancellationToken).ConfigureAwait(false);

            // 4. Cache results so the ChatAgentService can retrieve them for citations
            LastSearchResults.Clear();
            LastSearchResults.AddRange(reranked);

            // 5. Return results to Microsoft Agents AI reasoning loop
            return reranked.Select(c => new TextSearchProvider.TextSearchResult
            {
                SourceName = c.SourceName,
                SourceLink = c.SourceLink,
                Text = c.Text,
                RawRepresentation = c
            });
        }

        private async Task<List<SourceCitation>> GetDenseCandidatesAsync(string query, CancellationToken cancellationToken)
        {
            var vectors = EmbeddingsHelper.CreateVectors(
                [query],
                idSelector: _ => "query",
                contentSelector: q => q,
                apiKey: _openAIApiKey,
                options: _embeddingOptions,
                model: _embeddingModel);

            if (vectors.Count == 0)
            {
                return new List<SourceCitation>();
            }

            var queryResponse = await _pinecone.Index(_indexName).QueryAsync(
                new QueryRequest
                {
                    Vector = vectors[0].Values,
                    TopK = (uint)_topK,
                    IncludeMetadata = true,
                    Filter = _filter
                }).ConfigureAwait(false);

            cancellationToken.ThrowIfCancellationRequested();

            var matches = (queryResponse.Matches ?? []).ToList();
            var candidates = new List<SourceCitation>();

            foreach (var match in matches)
            {
                var metadata = match.Metadata ?? [];
                var text = GetMetadataValue(metadata, "Content")
                    ?? GetMetadataValue(metadata, "content")
                    ?? string.Empty;

                // Parent-Child Context Swap Logic
                var parentId = GetMetadataValue(metadata, "parentId");
                if (!string.IsNullOrEmpty(parentId))
                {
                    try
                    {
                        var parentChunk = _dbContext.ParentChunks.FirstOrDefault(p => p.Id == parentId);
                        if (parentChunk != null)
                        {
                            text = parentChunk.Content;
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[Parent-Child Swap ERROR] Failed to fetch parent chunk '{parentId}': {ex.Message}");
                    }
                }

                var chunkIndex = GetMetadataValue(metadata, "chunkIndex");
                var pageNumber = GetMetadataValue(metadata, "pageNumber");
                var sourceName = GetMetadataValue(metadata, "sourceName")
                    ?? BuildSourceName(match.Id, chunkIndex, pageNumber);
                sourceName = AddLocationToSourceName(sourceName, chunkIndex, pageNumber);
                var sourceLink = GetMetadataValue(metadata, "sourceLink");

                candidates.Add(new SourceCitation
                {
                    SourceName = sourceName,
                    SourceLink = sourceLink,
                    Text = text,
                    Score = match.Score
                });
            }

            return candidates;
        }

        private static string BuildSourceName(string id, string? chunkIndex, string? pageNumber)
        {
            return string.IsNullOrWhiteSpace(id) ? "Pinecone search result" : $"Pinecone chunk {id}";
        }

        private static string AddLocationToSourceName(string sourceName, string? chunkIndex, string? pageNumber)
        {
            if (!string.IsNullOrEmpty(pageNumber))
            {
                sourceName += $" page {pageNumber}";
            }
            else if (!string.IsNullOrEmpty(chunkIndex))
            {
                sourceName += $" chunk {chunkIndex}";
            }

            return sourceName;
        }

        private static string? GetMetadataValue(Metadata metadata, string key)
        {
            return metadata.TryGetValue(key, out var value) ? value?.ToString() : null;
        }
    }
}
