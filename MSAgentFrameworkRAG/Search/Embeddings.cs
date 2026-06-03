using System;
using System.Collections.Generic;
using System.Linq;
using OpenAI.Embeddings;
using Pinecone;

namespace MSAgentFrameworkRAG
{
    public static class EmbeddingsHelper
    {
        public static List<(string Id, float[] Values, Pinecone.Metadata Metadata)> CreateVectors<T>(
            IEnumerable<T> items,
            Func<T, string> idSelector,
            Func<T, string> contentSelector,
            string apiKey,
            EmbeddingGenerationOptions? options = null,
            string model = "text-embedding-3-small",
            Func<T, Pinecone.Metadata>? metadataBuilder = null)
        {
            var list = items.ToList();

            var embeddingClient = new EmbeddingClient(model, apiKey);

            var contents = list.Select(contentSelector).ToList();

            OpenAIEmbeddingCollection embeddings = embeddingClient.GenerateEmbeddings(contents, options).Value;

            var vectors = new List<(string Id, float[] Values, Pinecone.Metadata Metadata)>();

            foreach (var embedding in embeddings)
            {
                var idx = embedding.Index;
                var item = list[idx];

                float[] values = embedding.ToFloats().ToArray();

                Pinecone.Metadata metadata;
                if (metadataBuilder != null)
                {
                    metadata = metadataBuilder(item);
                }
                else
                {
                    metadata = new Pinecone.Metadata();
                    metadata["id"] = new Pinecone.MetadataValue(idSelector(item));
                    metadata["content"] = new Pinecone.MetadataValue(contentSelector(item));
                }

                vectors.Add((idSelector(item), values, metadata));
            }

            return vectors;
        }
    }
}
