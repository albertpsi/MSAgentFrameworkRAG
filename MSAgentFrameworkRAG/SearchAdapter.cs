using Microsoft.Agents.AI;
using OpenAI.Embeddings;
using Pinecone;

namespace MSAgentFrameworkRAG;

public sealed class PineconeTextSearchAdapter
{
    private readonly PineconeClient _pinecone;
    private readonly string _indexName;
    private readonly string _openAIApiKey;
    private readonly string _embeddingModel;
    private readonly EmbeddingGenerationOptions? _embeddingOptions;
    private readonly Metadata? _filter;
    private readonly int _topK;

    public PineconeTextSearchAdapter(
        PineconeClient pinecone,
        string indexName,
        string openAIApiKey,
        EmbeddingGenerationOptions? embeddingOptions = null,
        Metadata? filter = null,
        string embeddingModel = "text-embedding-3-small",
        int topK = 5)
    {
        _pinecone = pinecone ?? throw new ArgumentNullException(nameof(pinecone));
        _indexName = string.IsNullOrWhiteSpace(indexName) ? throw new ArgumentException("Index name is required.", nameof(indexName)) : indexName;
        _openAIApiKey = string.IsNullOrWhiteSpace(openAIApiKey) ? throw new ArgumentException("OpenAI API key is required.", nameof(openAIApiKey)) : openAIApiKey;
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

        var queryResponse = await _pinecone.Index(_indexName).QueryAsync(
            new QueryRequest
            {
                Vector = vectors[0].Values,
                TopK = (uint)_topK,
                IncludeMetadata = true,
                Filter = _filter
            }).ConfigureAwait(false);

        cancellationToken.ThrowIfCancellationRequested();

        return (queryResponse.Matches ?? []).Select(match =>
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

            return new TextSearchProvider.TextSearchResult
            {
                SourceName = sourceName,
                SourceLink = sourceLink,
                Text = text,
                RawRepresentation = match
            };
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
