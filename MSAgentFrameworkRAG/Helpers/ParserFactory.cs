using System;
using System.IO;
using MSAgentFrameworkRAG.Interfaces;

namespace MSAgentFrameworkRAG.Helpers
{
    public class PlainTextParser : IDocumentParser
    {
        public StructuredDocument Parse(string filePath)
        {
            var doc = new StructuredDocument { DocumentName = Path.GetFileName(filePath) };
            try
            {
                string text = File.ReadAllText(filePath);
                doc.Sections.Add(new TextSection
                {
                    PageOrSlideNumber = 1,
                    ParagraphText = text
                });
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[PlainTextParser ERROR] Failed to read plain text file '{filePath}': {ex.Message}");
            }
            return doc;
        }
    }

    public static class ParserFactory
    {
        public static IDocumentParser GetParser(string filePath)
        {
            string ext = Path.GetExtension(filePath).ToLowerInvariant();
            return ext switch
            {
                ".pdf" => new PdfParser(),
                ".docx" => new WordParser(),
                _ => new PlainTextParser()
            };
        }
    }
}
