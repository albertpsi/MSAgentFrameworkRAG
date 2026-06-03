using System;
using System.Collections.Generic;

public class TextChunk
{
    public int ChunkIndex { get; set; }

    public string Content { get; set; } = string.Empty;

    public int PageNumber { get; set; }

    public Dictionary<string, string> Metadata { get; set; } = new();

    /// <summary>
    /// Optional: Holds the full parent table content in Markdown/JSON format.
    /// Used by the ingestion service to save parent chunks in SQL Server.
    /// </summary>
    public string? ParentContent { get; set; }
}