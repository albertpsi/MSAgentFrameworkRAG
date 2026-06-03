using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace MSAgentFrameworkRAG
{
    public class StructuredDocument
    {
        public string DocumentName { get; set; } = string.Empty;
        public List<DocSection> Sections { get; set; } = new();
    }

    public abstract class DocSection
    {
        public int PageOrSlideNumber { get; set; }
    }

    // Represents standard text paragraphs
    public class TextSection : DocSection
    {
        public string ParagraphText { get; set; } = string.Empty;
    }

    // Represents structured grids or tables
    public class TableSection : DocSection
    {
        public List<string> Headers { get; set; } = new();
        public List<List<string>> Rows { get; set; } = new();

        public string ToMarkdown()
        {
            if (Headers.Count == 0 && Rows.Count == 0) return string.Empty;

            var sb = new StringBuilder();

            // Handle no-headers case gracefully
            var displayHeaders = Headers.Count > 0 
                ? Headers 
                : Enumerable.Range(1, Rows.Count > 0 ? Rows[0].Count : 1).Select(i => $"Column {i}").ToList();

            sb.AppendLine("| " + string.Join(" | ", displayHeaders) + " |");
            sb.AppendLine("| " + string.Join(" | ", displayHeaders.Select(_ => "---")) + " |");

            foreach (var row in Rows)
            {
                // Align row columns with header counts
                var cells = new List<string>(row);
                while (cells.Count < displayHeaders.Count)
                {
                    cells.Add(string.Empty);
                }
                if (cells.Count > displayHeaders.Count)
                {
                    cells = cells.Take(displayHeaders.Count).ToList();
                }

                sb.AppendLine("| " + string.Join(" | ", cells.Select(c => c?.Replace("|", "\\|") ?? string.Empty)) + " |");
            }

            return sb.ToString();
        }
    }
}
