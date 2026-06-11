using System;
using System.Collections.Generic;

namespace MSAgentFrameworkRAG
{
    public class ParsedDocument
    {
        public string DocumentId { get; set; } = string.Empty;
        public int PageCount { get; set; }
        public List<ParsedSection> Sections { get; set; } = new();
        public List<ParsedTable> Tables { get; set; } = new();
    }

    public class ParsedSection
    {
        public string Type { get; set; } = string.Empty; // heading, paragraph
        public string Text { get; set; } = string.Empty;
        public int PageNumber { get; set; }
    }

    public class ParsedTable
    {
        public int PageNumber { get; set; }
        public List<string> Headers { get; set; } = new();
        public List<List<string>> Rows { get; set; } = new();
    }
}
