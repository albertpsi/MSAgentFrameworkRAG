using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Presentation;
using DTable = DocumentFormat.OpenXml.Drawing.Table;
using DTableRow = DocumentFormat.OpenXml.Drawing.TableRow;
using DTableCell = DocumentFormat.OpenXml.Drawing.TableCell;
using DText = DocumentFormat.OpenXml.Drawing.Text;
using DParagraph = DocumentFormat.OpenXml.Drawing.Paragraph;
using MSAgentFrameworkRAG.Interfaces;

namespace MSAgentFrameworkRAG.Helpers
{
    public class PptParser : IDocumentParser
    {
        public StructuredDocument Parse(string filePath)
        {
            var doc = new StructuredDocument { DocumentName = Path.GetFileName(filePath) };

            try
            {
                using var presentationDoc = PresentationDocument.Open(filePath, false);
                var presentationPart = presentationDoc.PresentationPart;
                if (presentationPart == null) return doc;

                var slideIdList = presentationPart.Presentation.SlideIdList;
                if (slideIdList == null) return doc;

                int slideIndex = 1;
                foreach (SlideId slideId in slideIdList.Elements<SlideId>())
                {
                    if (slideId.RelationshipId == null) continue;
                    
                    var slidePart = presentationPart.GetPartById(slideId.RelationshipId.Value) as SlidePart;
                    if (slidePart?.Slide == null) continue;

                    // 1. First extract all drawing tables (a:tbl) on the slide
                    var drawingTables = slidePart.Slide.Descendants<DTable>().ToList();
                    foreach (var dTable in drawingTables)
                    {
                        var tableSection = new TableSection { PageOrSlideNumber = slideIndex };
                        var dRows = dTable.Elements<DTableRow>().ToList();

                        if (dRows.Count > 0)
                        {
                            // Parse headers from first drawing row
                            var headers = dRows[0].Elements<DTableCell>()
                                .Select(cell => GetCellText(cell))
                                .ToList();
                            tableSection.Headers = headers;

                            // Parse remaining data rows
                            for (int r = 1; r < dRows.Count; r++)
                            {
                                var rowCells = dRows[r].Elements<DTableCell>()
                                    .Select(cell => GetCellText(cell))
                                    .ToList();
                                tableSection.Rows.Add(rowCells);
                            }

                            doc.Sections.Add(tableSection);
                        }
                    }

                    // 2. Extract regular text paragraphs from other shapes
                    var slideTextBuilder = new StringBuilder();
                    var shapeTexts = slidePart.Slide.Descendants<Shape>()
                        .Select(s => s.TextBody)
                        .Where(tb => tb != null);

                    foreach (var textBody in shapeTexts)
                    {
                        if (textBody == null) continue;
                        foreach (var paragraph in textBody.Descendants<DParagraph>())
                        {
                            var paragraphText = string.Join("", paragraph.Descendants<DText>().Select(t => t.Text)).Trim();
                            if (!string.IsNullOrWhiteSpace(paragraphText))
                            {
                                slideTextBuilder.AppendLine(paragraphText);
                            }
                        }
                    }

                    string fullSlideText = slideTextBuilder.ToString().Trim();
                    if (!string.IsNullOrEmpty(fullSlideText))
                    {
                        doc.Sections.Add(new TextSection
                        {
                            PageOrSlideNumber = slideIndex,
                            ParagraphText = fullSlideText
                        });
                    }

                    slideIndex++;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[PptParser ERROR] Failed to parse PowerPoint document '{filePath}': {ex.Message}");
            }

            return doc;
        }

        private static string GetCellText(DTableCell cell)
        {
            if (cell == null) return string.Empty;
            
            var textBuilder = new StringBuilder();
            var paragraphs = cell.Descendants<DParagraph>();
            
            foreach (var paragraph in paragraphs)
            {
                var runTexts = paragraph.Descendants<DText>().Select(t => t.Text);
                textBuilder.Append(string.Join("", runTexts));
            }
            
            return textBuilder.ToString().Trim();
        }
    }
}
