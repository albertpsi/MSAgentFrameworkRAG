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
        private class LogicalCell
        {
            public string Text { get; set; } = string.Empty;
            public double Left { get; set; }
            public double Right { get; set; }
        }

        private class ColumnInterval
        {
            public double Left { get; set; }
            public double Right { get; set; }
        }

        /// <summary>
        /// Parses page text structures using horizontal alignment to reconstruct cells cleanly.
        /// Delimits cells with vertical pipes '|' to maintain table representation in RAG text chunks.
        /// Dynamically discovers column X-coordinates and propagates row-spanned cells across all columns.
        /// </summary>
        public string ExtractStructuredTable(Page page)
        {
            if (page == null) return string.Empty;

            var words = page.GetWords().ToList();
            if (!words.Any()) return string.Empty;

            var lines = GroupWordsIntoLines(words);
            if (!lines.Any()) return string.Empty;

            // 1. Group adjacent words horizontally on each baseline into logical cells
            var baselineCells = new List<List<LogicalCell>>();
            var allIntervals = new List<ColumnInterval>();

            foreach (var line in lines)
            {
                var rowCells = new List<LogicalCell>();
                int i = 0;

                while (i < line.Count)
                {
                    var cellWords = new List<Word> { line[i] };

                    while (i < line.Count - 1)
                    {
                        double gap = line[i + 1].BoundingBox.Left - line[i].BoundingBox.Right;
                        if (gap < 12.0) // 12 points corresponds to typical intra-cell spacing
                        {
                            cellWords.Add(line[i + 1]);
                            i++;
                        }
                        else
                        {
                            break;
                        }
                    }

                    var text = string.Join(" ", cellWords.Select(w => w.Text));
                    double left = cellWords.First().BoundingBox.Left;
                    double right = cellWords.Last().BoundingBox.Right;

                    var cell = new LogicalCell { Text = text, Left = left, Right = right };
                    rowCells.Add(cell);
                    
                    allIntervals.Add(new ColumnInterval { Left = left, Right = right });
                    i++;
                }

                if (rowCells.Any())
                {
                    baselineCells.Add(rowCells);
                }
            }

            if (!allIntervals.Any()) return string.Empty;

            // 2. Cluster / Merge close or overlapping X-intervals across the page to define Column Grids
            var sortedIntervals = allIntervals.OrderBy(intv => intv.Left).ToList();
            var columns = new List<ColumnInterval>();

            foreach (var intv in sortedIntervals)
            {
                if (!columns.Any())
                {
                    columns.Add(intv);
                }
                else
                {
                    var last = columns.Last();
                    // Merge intervals if they overlap or the gap is very small (within 15.0 points)
                    if (intv.Left <= last.Right || (intv.Left - last.Right) < 15.0)
                    {
                        last.Right = Math.Max(last.Right, intv.Right);
                    }
                    else
                    {
                        columns.Add(intv);
                    }
                }
            }

            int numCols = columns.Count;
            var activeRowValues = new string[numCols];
            for (int k = 0; k < numCols; k++) activeRowValues[k] = string.Empty;

            var sb = new StringBuilder();

            // 3. For each baseline, map cells to their respective column grids and forward-fill if blank
            foreach (var rowCells in baselineCells)
            {
                var alignedRow = new string?[numCols];

                foreach (var cell in rowCells)
                {
                    // Map cell to the column with the maximum overlap or closest proximity
                    int bestColIndex = -1;
                    double maxOverlap = -1.0;
                    double minDistance = double.MaxValue;

                    for (int k = 0; k < numCols; k++)
                    {
                        var col = columns[k];
                        // Calculate overlap length
                        double overlapStart = Math.Max(cell.Left, col.Left);
                        double overlapEnd = Math.Min(cell.Right, col.Right);
                        double overlap = overlapEnd - overlapStart;

                        if (overlap > 0)
                        {
                            if (overlap > maxOverlap)
                            {
                                maxOverlap = overlap;
                                bestColIndex = k;
                            }
                        }
                        else
                        {
                            // Fallback to center-to-center distance if no direct overlap (rare)
                            double cellCenter = cell.Left + (cell.Right - cell.Left) / 2.0;
                            double colCenter = col.Left + (col.Right - col.Left) / 2.0;
                            double dist = Math.Abs(cellCenter - colCenter);
                            if (dist < minDistance)
                            {
                                minDistance = dist;
                                if (maxOverlap <= 0) 
                                {
                                    bestColIndex = k;
                                }
                            }
                        }
                    }

                    if (bestColIndex != -1)
                    {
                        alignedRow[bestColIndex] = cell.Text;
                    }
                }

                // 4. Populate row cells using forward-filling for spanned columns
                var finalizedRow = new string[numCols];
                for (int k = 0; k < numCols; k++)
                {
                    if (alignedRow[k] != null)
                    {
                        activeRowValues[k] = alignedRow[k]!;
                        finalizedRow[k] = alignedRow[k]!;
                    }
                    else
                    {
                        // Column is blank on this baseline, propagate previous active value!
                        finalizedRow[k] = activeRowValues[k];
                    }
                }

                sb.AppendLine(string.Join(" | ", finalizedRow));
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
