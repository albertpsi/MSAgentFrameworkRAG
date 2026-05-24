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
        private readonly int _topK;

        // Stores the exact reranked, score-filtered chunks used by the LLM
        // to be reused directly as citations in the UI without making a second search
        public List<SourceCitation> LastSearchResults { get; } = new();

        public PineconeTextSearchAdapter(
            PineconeClient pinecone,
            string indexName,
            string openAIApiKey,
            IRerankService rerankService,
            EmbeddingGenerationOptions? embeddingOptions = null,
            Metadata? filter = null,
            string embeddingModel = "text-embedding-3-small",
            int topK = 40)
        {
            _pinecone = pinecone ?? throw new ArgumentNullException(nameof(pinecone));
            _indexName = string.IsNullOrWhiteSpace(indexName) ? throw new ArgumentException("Index name is required.", nameof(indexName)) : indexName;
            _openAIApiKey = string.IsNullOrWhiteSpace(openAIApiKey) ? throw new ArgumentException("OpenAI API key is required.", nameof(openAIApiKey)) : openAIApiKey;
            _rerankService = rerankService ?? throw new ArgumentNullException(nameof(rerankService));
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

            // 1. Generate Query Embeddings
            var vectors = EmbeddingsHelper.CreateVectors(
                [query],
                idSelector: _ => "query",
                contentSelector: q => q,
                apiKey: _openAIApiKey,
                options: _embeddingOptions,
                model: _embeddingModel);

            if (vectors.Count == 0)
            {
                return [];
            }

            // 2. Coarse Vector Search (Stage 1) - Cast a wide net (TopK = 40)
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

                var chunkIndex = GetMetadataValue(metadata, "chunkIndex");
                var pageNumber = GetMetadataValue(metadata, "pageNumber");
                var sourceName = GetMetadataValue(metadata, "sourceName")
                    ?? BuildSourceName(match.Id, chunkIndex, pageNumber);
                var sourceLink = GetMetadataValue(metadata, "sourceLink");

                candidates.Add(new SourceCitation
                {
                    SourceName = sourceName,
                    SourceLink = sourceLink,
                    Text = text,
                    Score = match.Score // Store raw similarity score
                });
            }

            // 3. Fine Native Reranking & Score Filtering (Stage 2)
            var reranked = await _rerankService.RerankAsync(query, candidates, cancellationToken).ConfigureAwait(false);

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

        private static string BuildSourceName(string id, string? chunkIndex, string? pageNumber)
        {
            var sourceName = string.IsNullOrWhiteSpace(id) ? "Pinecone search result" : $"Pinecone chunk {id}";

            if (!string.IsNullOrWhiteSpace(pageNumber))
            {
                sourceName += $" page {pageNumber}";
            }
            else if (!string.IsNullOrWhiteSpace(chunkIndex))
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
