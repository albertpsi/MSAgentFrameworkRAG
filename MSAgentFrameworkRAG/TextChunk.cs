public class TextChunk
{
    public int ChunkIndex { get; set; }

    public string Content { get; set; } = string.Empty;

    public int PageNumber { get; set; }

    public Dictionary<string, string> Metadata { get; set; } = new();
}