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
                for (int i = 0; i < line.Words.Count - 1; i++)
                {
                    double gap = line.Words[i + 1].BoundingBox.Left - line.Words[i].BoundingBox.Right;
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

        private class BaselineLine
        {
            public List<Word> Words { get; set; } = new();
            public double Y { get; set; }
        }

        private class BaselineCellsWithY
        {
            public List<LogicalCell> Cells { get; set; } = new();
            public double Y { get; set; }
        }

        private class AlignedRowWithY
        {
            public string?[] Cells { get; set; } = null!;
            public double Y { get; set; }
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
            var baselineCells = new List<BaselineCellsWithY>();
            var allIntervals = new List<ColumnInterval>();

            foreach (var line in lines)
            {
                var rowCells = new List<LogicalCell>();
                int i = 0;

                while (i < line.Words.Count)
                {
                    var cellWords = new List<Word> { line.Words[i] };

                    while (i < line.Words.Count - 1)
                    {
                        double gap = line.Words[i + 1].BoundingBox.Left - line.Words[i].BoundingBox.Right;
                        if (gap < 12.0) // 12 points corresponds to typical intra-cell spacing
                        {
                            cellWords.Add(line.Words[i + 1]);
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
                    i++;
                }

                if (rowCells.Any())
                {
                    baselineCells.Add(new BaselineCellsWithY { Cells = rowCells, Y = line.Y });
                    
                    // Only use lines with multiple cells to define the column grids.
                    // This prevents single full-width lines (e.g. paragraphs, page titles) from merging columns.
                    if (rowCells.Count > 1)
                    {
                        foreach (var cell in rowCells)
                        {
                            allIntervals.Add(new ColumnInterval { Left = cell.Left, Right = cell.Right });
                        }
                    }
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

            // 3. For each baseline, map cells to their respective column grids
            var alignedRows = new List<AlignedRowWithY>();

            foreach (var rowInfo in baselineCells)
            {
                var alignedRow = new string?[numCols];

                foreach (var cell in rowInfo.Cells)
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

                alignedRows.Add(new AlignedRowWithY { Cells = alignedRow, Y = rowInfo.Y });
            }

            // 4. Combine consecutive rows representing multi-line wrapped cells based on vertical gap (under 20.0 points)
            var mergedRows = new List<string?[]>();
            if (alignedRows.Any())
            {
                var currentMergedRow = new string?[numCols];
                Array.Copy(alignedRows[0].Cells, currentMergedRow, numCols);
                double lastY = alignedRows[0].Y;

                for (int r = 1; r < alignedRows.Count; r++)
                {
                    var row = alignedRows[r];
                    double gap = lastY - row.Y;

                    if (gap < 20.0)
                    {
                        // Merge current row cells with the active merged row cells
                        for (int k = 0; k < numCols; k++)
                        {
                            if (row.Cells[k] != null)
                            {
                                if (currentMergedRow[k] != null)
                                {
                                    currentMergedRow[k] = (currentMergedRow[k] + " " + row.Cells[k]).Trim();
                                }
                                else
                                {
                                    currentMergedRow[k] = row.Cells[k];
                                }
                            }
                        }
                    }
                    else
                    {
                        // Commit the current merged row
                        mergedRows.Add(currentMergedRow);

                        // Start a new merged row
                        currentMergedRow = new string?[numCols];
                        Array.Copy(row.Cells, currentMergedRow, numCols);
                    }

                    lastY = row.Y;
                }
                mergedRows.Add(currentMergedRow);
            }

            var activeRowValues = new string[numCols];
            for (int k = 0; k < numCols; k++) activeRowValues[k] = string.Empty;

            var sb = new StringBuilder();

            // 5. Populate row cells using forward-filling for spanned columns
            foreach (var rowCells in mergedRows)
            {
                var finalizedRow = new string[numCols];
                for (int k = 0; k < numCols; k++)
                {
                    if (rowCells[k] != null)
                    {
                        activeRowValues[k] = rowCells[k]!;
                        finalizedRow[k] = rowCells[k]!;
                    }
                    else
                    {
                        // ONLY forward-fill Column 0 (e.g. Card Names) for spanned rows.
                        // Other columns (fees, spend limits) should remain empty if blank in the PDF.
                        if (k == 0)
                        {
                            finalizedRow[k] = activeRowValues[k];
                        }
                        else
                        {
                            finalizedRow[k] = string.Empty;
                        }
                    }
                }

                sb.AppendLine(string.Join(" | ", finalizedRow));
            }

            return sb.ToString();
        }

        private List<BaselineLine> GroupWordsIntoLines(List<Word> words)
        {
            var lines = new List<BaselineLine>();
            var sortedWords = words.OrderByDescending(w => w.BoundingBox.Bottom).ToList();

            while (sortedWords.Count > 0)
            {
                var currentWord = sortedWords[0];
                double baselineY = currentWord.BoundingBox.Bottom;
                
                // Group words sitting within 4.0 points vertical difference of the baseline
                var lineWords = sortedWords
                    .Where(w => Math.Abs(w.BoundingBox.Bottom - baselineY) <= 4.0)
                    .OrderBy(w => w.BoundingBox.Left)
                    .ToList();

                lines.Add(new BaselineLine { Words = lineWords, Y = baselineY });
                sortedWords.RemoveAll(lineWords.Contains);
            }

            return lines;
        }
    }
}
