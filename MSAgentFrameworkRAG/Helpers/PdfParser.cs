using UglyToad.PdfPig;
using System;
using System.IO;
using MSAgentFrameworkRAG.Interfaces;

namespace MSAgentFrameworkRAG.Helpers
{
    public class PdfParser : IDocumentParser
    {
        public StructuredDocument Parse(string filePath)
        {
            var doc = new StructuredDocument { DocumentName = Path.GetFileName(filePath) };

            try
            {
                using var pdf = PdfDocument.Open(filePath);
                foreach (var page in pdf.GetPages())
                {
                    if (string.IsNullOrWhiteSpace(page.Text))
                    {
                        continue;
                    }

                    doc.Sections.Add(new TextSection
                    {
                        PageOrSlideNumber = page.Number,
                        ParagraphText = page.Text
                    });
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[PdfParser ERROR] Failed to parse PDF document '{filePath}': {ex.Message}");
            }

            return doc;
        }
    }
}
