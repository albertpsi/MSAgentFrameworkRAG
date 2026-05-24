using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using MSAgentFrameworkRAG.Interfaces;

namespace MSAgentFrameworkRAG.Helpers
{
    public class WordParser : IDocumentParser
    {
        public StructuredDocument Parse(string filePath)
        {
            var doc = new StructuredDocument { DocumentName = Path.GetFileName(filePath) };

            try
            {
                using var wordDoc = WordprocessingDocument.Open(filePath, false);
                var body = wordDoc.MainDocumentPart?.Document.Body;
                if (body == null) return doc;

                int paragraphIndex = 0;
                int pageNum = 1; // Word documents don't have rigid physical page numbers easily accessible in OpenXML, we use logical page/index grouping

                // Iterate through direct children of body in order to preserve sequential document flow
                foreach (var element in body.ChildElements)
                {
                    if (element is Paragraph p)
                    {
                        string pText = p.InnerText;
                        if (!string.IsNullOrWhiteSpace(pText))
                        {
                            doc.Sections.Add(new TextSection
                            {
                                PageOrSlideNumber = pageNum,
                                ParagraphText = pText.Trim()
                            });
                            
                            paragraphIndex++;
                            // Simple page demarcation heuristic (every 10 paragraphs represents a logical page)
                            if (paragraphIndex % 10 == 0)
                            {
                                pageNum++;
                            }
                        }
                    }
                    else if (element is Table table)
                    {
                        var tableSection = new TableSection { PageOrSlideNumber = pageNum };
                        var rows = table.Elements<TableRow>().ToList();

                        if (rows.Count > 0)
                        {
                            // 1. First Row is treated as Table Headers
                            var firstRowCells = rows[0].Elements<TableCell>().Select(c => c.InnerText.Trim()).ToList();
                            tableSection.Headers = firstRowCells;

                            // 2. Subsequent Rows
                            for (int r = 1; r < rows.Count; r++)
                            {
                                var cellTexts = rows[r].Elements<TableCell>().Select(c => c.InnerText.Trim()).ToList();
                                tableSection.Rows.Add(cellTexts);
                            }

                            doc.Sections.Add(tableSection);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[WordParser ERROR] Failed to parse word document '{filePath}': {ex.Message}");
                // Fallback: Read plain text if zip reading fails or file is locked
                try
                {
                    string fallbackText = File.ReadAllText(filePath);
                    doc.Sections.Add(new TextSection
                    {
                        PageOrSlideNumber = 1,
                        ParagraphText = fallbackText
                    });
                }
                catch { /* Ignore double fail */ }
            }

            return doc;
        }
    }
}
