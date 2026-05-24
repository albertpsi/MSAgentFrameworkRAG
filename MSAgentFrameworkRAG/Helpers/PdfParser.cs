using UglyToad.PdfPig;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MSAgentFrameworkRAG.Interfaces;
using MSAgentFrameworkRAG.Services;

namespace MSAgentFrameworkRAG.Helpers
{
    public class PdfParser : IDocumentParser
    {
        private readonly PdfLayoutAnalysisService _layoutService = new();

        public StructuredDocument Parse(string filePath)
        {
            var doc = new StructuredDocument { DocumentName = Path.GetFileName(filePath) };

            using var pdf = PdfDocument.Open(filePath);
            foreach (var page in pdf.GetPages())
            {
                if (_layoutService.DetectTableOnPage(page))
                {
                    var tableString = _layoutService.ExtractStructuredTable(page);
                    var lines = tableString.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.RemoveEmptyEntries);
                    
                    if (lines.Length > 0)
                    {
                        var tableSection = new TableSection { PageOrSlideNumber = page.Number };
                        
                        // Parse headers from the first pipe-separated line
                        var firstRow = lines[0].Split('|').Select(h => h.Trim()).ToList();
                        
                        // Heuristic to check if first row is a header:
                        // A header row usually contains short text, not long descriptive lines, and is not all empty
                        bool isHeader = firstRow.Count > 0 && !firstRow.All(string.IsNullOrWhiteSpace);
                        
                        if (isHeader)
                        {
                            tableSection.Headers = firstRow;
                            for (int i = 1; i < lines.Length; i++)
                            {
                                var rowCells = lines[i].Split('|').Select(c => c.Trim()).ToList();
                                tableSection.Rows.Add(rowCells);
                            }
                        }
                        else
                        {
                            // If it's not a header row (or table has no headers), threat all lines as rows
                            for (int i = 0; i < lines.Length; i++)
                            {
                                var rowCells = lines[i].Split('|').Select(c => c.Trim()).ToList();
                                tableSection.Rows.Add(rowCells);
                            }
                        }
                        
                        doc.Sections.Add(tableSection);
                    }
                }
                else
                {
                    doc.Sections.Add(new TextSection
                    {
                        PageOrSlideNumber = page.Number,
                        ParagraphText = page.Text
                    });
                }
            }

            return doc;
        }
    }
}
