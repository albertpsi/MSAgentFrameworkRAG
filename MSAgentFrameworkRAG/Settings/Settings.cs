namespace MSAgentFrameworkRAG
{
    public class OpenAISettings
    {
        public const string Position = "OpenAI";
        public string ApiKey { get; set; } = string.Empty;
        public string ChatModel { get; set; } = "gpt-4o-mini";
        public string RagChatModel { get; set; } = "gpt-5-mini";
        public string EmbeddingModel { get; set; } = "text-embedding-3-large";
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

    public class ParserSettings
    {
        public const string Position = "Parser";
        public string Provider { get; set; } = "Docling";
        public string DoclingWorkerUrl { get; set; } = "http://localhost:8000";
    }

    public class StorageSettings
    {
        public const string Position = "Storage";
        public string RootDirectory { get; set; } = "wwwroot/storage";

        public string DocumentsDirectory => System.IO.Path.Combine(RootDirectory, "documents");
        public string ParsedDirectory => System.IO.Path.Combine(RootDirectory, "parsed");
        public string ChunksDirectory => System.IO.Path.Combine(RootDirectory, "chunks");
    }
}
