namespace MSAgentFrameworkRAG
{
    public class OpenAISettings
    {
        public const string Position = "OpenAI";
        public string ApiKey { get; set; } = string.Empty;
        public string ChatModel { get; set; } = "gpt-4o-mini";
        public string RagChatModel { get; set; } = "gpt-5-mini";
        public string EmbeddingModel { get; set; } = "text-embedding-3-small";
    }

    public class PineconeSettings
    {
        public const string Position = "Pinecone";
        public string ApiKey { get; set; } = string.Empty;
        public string IndexName { get; set; } = string.Empty;
        public string RerankModel { get; set; } = "bge-reranker-v2-m3";
        public double RerankScoreThreshold { get; set; } = 0.5;
        public int RerankTopN { get; set; } = 10;
        public int QueryTopK { get; set; } = 40;
    }
}
