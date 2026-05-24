using System;
using System.Collections.Generic;
using System.Data;
using System.IO;
using System.Linq;
using ExcelDataReader;
using MSAgentFrameworkRAG.Interfaces;

namespace MSAgentFrameworkRAG.Helpers
{
    public class ExcelParser : IDocumentParser
    {
        static ExcelParser()
        {
            // Required to register encoding providers for older .xls and various character encodings in ExcelDataReader
            System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);
        }

        public StructuredDocument Parse(string filePath)
        {
            var doc = new StructuredDocument { DocumentName = Path.GetFileName(filePath) };

            try
            {
                using var stream = File.Open(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var reader = ExcelReaderFactory.CreateReader(stream);
                
                var result = reader.AsDataSet(new ExcelDataSetConfiguration()
                {
                    ConfigureDataTable = (_) => new ExcelDataTableConfiguration()
                    {
                        UseHeaderRow = false // Read raw first row manually to handle empty cases gracefully
                    }
                });

                int sheetIndex = 1;
                foreach (DataTable table in result.Tables)
                {
                    string sheetName = table.TableName;
                    if (table.Rows.Count == 0) continue;

                    var tableSection = new TableSection 
                    { 
                        PageOrSlideNumber = sheetIndex++
                    };

                    // Extract the first row as potential headers
                    var firstRow = table.Rows[0];
                    var headers = new List<string>();
                    for (int c = 0; c < table.Columns.Count; c++)
                    {
                        var val = firstRow[c]?.ToString()?.Trim() ?? string.Empty;
                        headers.Add(val);
                    }

                    // Check if headers is valid, otherwise use Column 1, Column 2 etc.
                    bool hasValidHeaders = headers.Count > 0 && !headers.All(string.IsNullOrWhiteSpace);
                    
                    if (hasValidHeaders)
                    {
                        // Add Sheet Info to first header cell to provide spreadsheet context in retrieved chunks
                        headers[0] = $"[Sheet: {sheetName}] {headers[0]}";
                        tableSection.Headers = headers;

                        // Add subsequent rows
                        for (int r = 1; r < table.Rows.Count; r++)
                        {
                            var rowCells = new List<string>();
                            for (int c = 0; c < table.Columns.Count; c++)
                            {
                                rowCells.Add(table.Rows[r][c]?.ToString()?.Trim() ?? string.Empty);
                            }
                            tableSection.Rows.Add(rowCells);
                        }
                    }
                    else
                    {
                        // No headers: use generic headers
                        var genericHeaders = Enumerable.Range(1, table.Columns.Count)
                            .Select(colIdx => colIdx == 1 ? $"[Sheet: {sheetName}] Column 1" : $"Column {colIdx}")
                            .ToList();
                        
                        tableSection.Headers = genericHeaders;

                        // All rows are treated as data rows
                        for (int r = 0; r < table.Rows.Count; r++)
                        {
                            var rowCells = new List<string>();
                            for (int c = 0; c < table.Columns.Count; c++)
                            {
                                rowCells.Add(table.Rows[r][c]?.ToString()?.Trim() ?? string.Empty);
                            }
                            tableSection.Rows.Add(rowCells);
                        }
                    }

                    doc.Sections.Add(tableSection);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ExcelParser ERROR] Failed to parse Excel document '{filePath}': {ex.Message}");
            }

            return doc;
        }
    }
}
