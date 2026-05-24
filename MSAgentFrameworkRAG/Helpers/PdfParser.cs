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
            TableSection? activeTable = null;

            using var pdf = PdfDocument.Open(filePath);
            foreach (var page in pdf.GetPages())
            {
                if (_layoutService.DetectTableOnPage(page))
                {
                    var tableString = _layoutService.ExtractStructuredTable(page);
                    var lines = tableString.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.RemoveEmptyEntries);
                    
                    if (lines.Length > 0)
                    {
                        var parsedRows = lines.Select(l => l.Split('|').Select(c => c.Trim()).ToList()).ToList();
                        
                        // Check if this table is a continuation of the active table from the previous page
                        if (activeTable != null && parsedRows.Any() && parsedRows[0].Count == activeTable.Headers.Count)
                        {
                            // It has the same number of columns! 
                            // Check if the first row is a repeated header
                            bool isRepeatedHeader = parsedRows[0].SequenceEqual(activeTable.Headers, StringComparer.OrdinalIgnoreCase);
                            
                            int startIdx = isRepeatedHeader ? 1 : 0;
                            for (int i = startIdx; i < parsedRows.Count; i++)
                            {
                                activeTable.Rows.Add(parsedRows[i]);
                            }
                            
                            // Do NOT add a new section, we just appended rows to activeTable!
                            continue;
                        }
                        
                        // Otherwise, create a new table section
                        var tableSection = new TableSection { PageOrSlideNumber = page.Number };
                        
                        var firstRow = parsedRows[0];
                        // Heuristic to check if first row is a header:
                        bool isHeader = firstRow.Count > 0 && !firstRow.All(string.IsNullOrWhiteSpace);
                        
                        if (isHeader)
                        {
                            tableSection.Headers = firstRow;
                            for (int i = 1; i < parsedRows.Count; i++)
                            {
                                tableSection.Rows.Add(parsedRows[i]);
                            }
                        }
                        else
                        {
                            // Fallback headers
                            tableSection.Headers = firstRow.Select((_, idx) => $"Column_{idx}").ToList();
                            for (int i = 0; i < parsedRows.Count; i++)
                            {
                                tableSection.Rows.Add(parsedRows[i]);
                            }
                        }
                        
                        doc.Sections.Add(tableSection);
                        activeTable = tableSection; // Set as the active table for potential continuation
                    }
                }
                else
                {
                    doc.Sections.Add(new TextSection
                    {
                        PageOrSlideNumber = page.Number,
                        ParagraphText = page.Text
                    });
                    activeTable = null; // Text section breaks the table continuity
                }
            }

            return doc;
        }
    }
}
