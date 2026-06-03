using System;

namespace MSAgentFrameworkRAG
{
    public class DbParentChunk
    {
        public string Id { get; set; } = Guid.NewGuid().ToString();

        public string DocumentId { get; set; } = string.Empty;

        public string Content { get; set; } = string.Empty;

        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    }
}
