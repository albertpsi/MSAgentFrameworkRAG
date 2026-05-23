namespace MSAgentFrameworkRAG
{
    public class OpenAISettings
    {
        public const string Position = "OpenAI";
        public string ApiKey { get; set; } = string.Empty;
        public string ChatModel { get; set; } = "gpt-4o-mini";
        public string EmbeddingModel { get; set; } = "text-embedding-3-small";
    }

    public class PineconeSettings
    {
        public const string Position = "Pinecone";
        public string ApiKey { get; set; } = string.Empty;
        public string IndexName { get; set; } = string.Empty;
    }
}
