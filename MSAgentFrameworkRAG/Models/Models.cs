using System;
using System.Collections.Generic;

namespace MSAgentFrameworkRAG
{
    public class UploadedDocument
    {
        public string Id { get; set; } = string.Empty;
        public string FileName { get; set; } = string.Empty;
        public string Status { get; set; } = "Pending"; // Pending, Processing, Indexed, Failed
        public string? ErrorMessage { get; set; }
        public DateTime UploadedAt { get; set; } = DateTime.UtcNow;

        // Contract metadata fields
        public string? PartyA { get; set; }
        public string? PartyB { get; set; }
        public string? AgreementTitle { get; set; }
        public string? AgreementType { get; set; }
        public string? EffectiveDate { get; set; }
        public string? ExecutionDate { get; set; }
        public string? ExpirationDate { get; set; }
        public string? GoverningLaw { get; set; }
        public string? Jurisdiction { get; set; }
        public string? ContractStatus { get; set; }
        public string? AmendmentNumber { get; set; }
        public string? SupersedesDocument { get; set; }
        public string? ContractMetadataJson { get; set; }
        public string? Version { get; set; }
        public bool IsLatest { get; set; } = false;
        public int ChunkCount { get; set; } = 0;

        // Navigation property for parent visual chunks
        public List<DbParentChunk> ParentChunks { get; set; } = new();
    }

    public class ChatMessageInfo
    {
        public string Sender { get; set; } = "user"; // user, assistant
        public string Text { get; set; } = string.Empty;
        public DateTime Timestamp { get; set; } = DateTime.UtcNow;
        public List<SourceCitation>? Citations { get; set; }
    }

    public class SourceCitation
    {
        public string SourceName { get; set; } = string.Empty;
        public string? SourceLink { get; set; }
        public string Text { get; set; } = string.Empty;
        public double? Score { get; set; }
        public double? RerankScore { get; set; }
        public double? QueryTimeMs { get; set; }
        public string? ParentId { get; set; }
    }

    public class Conversation
    {
        public string Id { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public List<ChatMessageInfo> Messages { get; set; } = new();
    }

    public class ChatRequest
    {
        public string ConversationId { get; set; } = string.Empty;
        public string Message { get; set; } = string.Empty;
        public List<string>? DocumentIds { get; set; } // Optional filters
    }

    public class ChatResponse
    {
        public string ConversationId { get; set; } = string.Empty;
        public ChatMessageInfo Message { get; set; } = new();
    }
}
