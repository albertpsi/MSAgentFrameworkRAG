using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace MSAgentFrameworkRAG.Services
{
    public class PdfLayoutAnalysisService
    {
        /// <summary>
        /// Analyzes a PDF page dynamically using horizontal and vertical layout coordinates
        /// to determine if it contains a structured grid or table pattern.
        /// </summary>
        public bool DetectTableOnPage(Page page)
        {
            if (page == null) return false;

            var words = page.GetWords().ToList();
            if (words.Count < 10) return false;

            // 1. Group words on the page into horizontal lines based on Y coordinate alignment
            var lines = GroupWordsIntoLines(words);
            if (lines.Count == 0) return false;

            int multiColumnLineCount = 0;
            double largeGapThreshold = 25.0; // 25 points indicates columns

            foreach (var line in lines)
            {
                int gaps = 0;
                for (int i = 0; i < line.Count - 1; i++)
                {
                    double gap = line[i + 1].BoundingBox.Left - line[i].BoundingBox.Right;
                    if (gap >= largeGapThreshold)
                    {
                        gaps++;
                    }
                }

                // If a line has 2 or more large gaps, it contains at least 3 distinct column blocks
                if (gaps >= 2)
                {
                    multiColumnLineCount++;
                }
            }

            // Heuristic: If more than 20% of the page lines or at least 5 lines are multi-column,
            // the page contains structured tabular data.
            double ratio = (double)multiColumnLineCount / lines.Count;
            return multiColumnLineCount >= 5 || ratio >= 0.25;
        }

        /// <summary>
        /// Parses page text structures using horizontal alignment to reconstruct cells cleanly.
        /// Delimits cells with vertical pipes '|' to maintain table representation in RAG text chunks.
        /// Automatically detects and propagates row-spanned Column 0 (e.g. Card Names) using X-offset forward-filling.
        /// </summary>
        public string ExtractStructuredTable(Page page)
        {
            if (page == null) return string.Empty;

            var words = page.GetWords().ToList();
            if (!words.Any()) return string.Empty;

            var lines = GroupWordsIntoLines(words);
            if (!lines.Any()) return string.Empty;

            // Detect the X-coordinate of the left-most column margin (Column 0)
            double minLeft = lines.Where(l => l.Any()).Min(l => l[0].BoundingBox.Left);
            double leftColumnMarginThreshold = minLeft + 15.0; // 15 points tolerance

            var sb = new StringBuilder();
            string activeEntityName = string.Empty; // Caches Column 0 (e.g. Paytm HDFC Bank)

            foreach (var line in lines)
            {
                var rowCells = new List<string>();
                int i = 0;

                // 1. Group adjacent words horizontally into cells based on spacing
                while (i < line.Count)
                {
                    var cellWords = new List<string> { line[i].Text };

                    // Group adjacent words horizontally into a single logical cell
                    while (i < line.Count - 1)
                    {
                        double gap = line[i + 1].BoundingBox.Left - line[i].BoundingBox.Right;
                        if (gap < 12.0) // 12 points gap corresponds to typical intra-cell spacing
                        {
                            cellWords.Add(line[i + 1].Text);
                            i++;
                        }
                        else
                        {
                            break;
                        }
                    }

                    rowCells.Add(string.Join(" ", cellWords));
                    i++;
                }

                if (rowCells.Count == 0) continue;

                // 2. Row-Span Detection and Forward-Filling
                double firstWordLeft = line[0].BoundingBox.Left;

                if (firstWordLeft < leftColumnMarginThreshold)
                {
                    // Column 0 is present on this baseline. Cache it as the active entity name.
                    activeEntityName = rowCells[0];
                }
                else
                {
                    // Column 0 is blank on this line due to row-spanning (starts shifted to the right).
                    // Forward-fill by prepending the cached active entity name to maintain RAG context!
                    rowCells.Insert(0, activeEntityName + " (cont.)");
                }

                // Output formatted structured rows
                sb.AppendLine(string.Join(" | ", rowCells));
            }

            return sb.ToString();
        }

        private List<List<Word>> GroupWordsIntoLines(List<Word> words)
        {
            var lines = new List<List<Word>>();
            var sortedWords = words.OrderByDescending(w => w.BoundingBox.Bottom).ToList();

            while (sortedWords.Count > 0)
            {
                var currentWord = sortedWords[0];
                
                // Group words sitting within 4.0 points vertical difference of the baseline
                var lineWords = sortedWords
                    .Where(w => Math.Abs(w.BoundingBox.Bottom - currentWord.BoundingBox.Bottom) <= 4.0)
                    .OrderBy(w => w.BoundingBox.Left)
                    .ToList();

                lines.Add(lineWords);
                sortedWords.RemoveAll(lineWords.Contains);
            }

            return lines;
        }
    }
}
