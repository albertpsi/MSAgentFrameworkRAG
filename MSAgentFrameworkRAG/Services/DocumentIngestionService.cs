using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Options;
using OpenAI.Embeddings;
using Pinecone;
using MSAgentFrameworkRAG.Interfaces;

namespace MSAgentFrameworkRAG.Services
{
    public class DocumentIngestionService : IDocumentIngestionService
    {
        private readonly IDocumentService _documentService;
        private readonly IMetadataExtractionService _metadataService;
        private readonly AppDbContext _dbContext;
        private readonly OpenAISettings _openAiSettings;
        private readonly PineconeSettings _pineconeSettings;


        public DocumentIngestionService(
            IDocumentService documentService,
            IMetadataExtractionService metadataService,
            AppDbContext dbContext,
            IOptions<OpenAISettings> openAiOptions,
            IOptions<PineconeSettings> pineconeOptions)
        {
            _documentService = documentService ?? throw new ArgumentNullException(nameof(documentService));
            _metadataService = metadataService ?? throw new ArgumentNullException(nameof(metadataService));
            _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
            _openAiSettings = openAiOptions?.Value ?? throw new ArgumentNullException(nameof(openAiOptions));
            _pineconeSettings = pineconeOptions?.Value ?? throw new ArgumentNullException(nameof(pineconeOptions));
        }

        public async Task IngestDocumentAsync(string documentId, string filePath, string fileName)
        {
            Console.WriteLine($"[Document Ingestion] Starting ingestion for '{fileName}' ({documentId})...");

            var doc = _documentService.Get(documentId);
            if (doc == null)
            {
                Console.WriteLine($"[Document Ingestion Error] Document {documentId} not found in store.");
                return;
            }

            try
            {
                doc.Status = "Processing";
                _documentService.AddOrUpdate(doc);

                // 1. Extract Semantic Metadata
                Console.WriteLine($"[Document Ingestion] Running Metadata Extraction Agent...");
                var metadata = await _metadataService.ExtractMetadataAsync(filePath, fileName).ConfigureAwait(false);
                
                doc.FileName = metadata?.Result?.FileName ?? fileName;
                doc.Company = metadata?.Result?.Company ?? "Unknown";
                doc.DocumentType = metadata?.Result?.DocumentType ?? "Unknown";
                doc.FiscalQuarter = metadata?.Result?.FiscalQuarter ?? "N/A";
                doc.FiscalYear = metadata?.Result?.FiscalYear ?? 0;
                doc.PublicationDate = metadata?.Result?.PublicationDate ?? "Unknown";
                doc.Version = metadata?.Result?.Version ?? "1.0";

                Console.WriteLine($"[Document Ingestion] Metadata parsed: Company={doc.Company}, Type={doc.DocumentType}, Year={doc.FiscalYear}, Version={doc.Version}");

                // 2. Parse and chunk the file using the Generic Document Extraction Framework
                Console.WriteLine($"[Document Ingestion] Parsing and chunking document via unified framework...");
                var parser = Helpers.ParserFactory.GetParser(filePath);
                var structuredDoc = parser.Parse(filePath);
                var chunks = ChunkStructuredDocument(structuredDoc, chunkSize: 3000, overlap: 500);
                doc.ChunkCount = chunks.Count;

                Console.WriteLine($"[Document Ingestion] Split document into {doc.ChunkCount} chunks.");

                // 3. Process Versioning (Fuzzy Document Family Matching)
                Console.WriteLine($"[Document Ingestion] Processing version control...");
                var allDocs = _documentService.GetAll();
                
                // Group documents by same family (same company and similar type/name)
                var familyDocs = allDocs
                    .Where(d => d.Id != documentId && d.Status != "Failed" && IsSameDocumentFamily(d, doc))
                    .ToList();

                familyDocs.Add(doc); // Add current doc to the list to compare

                // Sort family docs descending (freshest first)
                var sortedDocs = familyDocs.OrderByDescending(d => d, new UploadedDocumentVersionComparer()).ToList();
                
                // Initialize Pinecone Client for vector updates
                var pinecone = new PineconeClient(_pineconeSettings.ApiKey);
                var index = pinecone.Index(_pineconeSettings.IndexName);

                // Update isLatest flag across the family
                for (int i = 0; i < sortedDocs.Count; i++)
                {
                    var d = sortedDocs[i];
                    bool shouldBeLatest = (i == 0);

                    if (d.Id == documentId)
                    {
                        doc.IsLatest = shouldBeLatest;
                    }
                    else if (d.IsLatest != shouldBeLatest)
                    {
                        Console.WriteLine($"[Document Ingestion] Version shift: document '{d.FileName}' ({d.Id}) IsLatest changed from {d.IsLatest} to {shouldBeLatest}. Updating vectors in Pinecone...");
                        d.IsLatest = shouldBeLatest;
                        _documentService.AddOrUpdate(d);

                        // Update Pinecone vectors for the older document only if it was already indexed
                        if (d.Status == "Indexed")
                        {
                            await UpdatePineconeLatestMetadataAsync(index, d.Id, d.ChunkCount, shouldBeLatest).ConfigureAwait(false);
                        }
                    }
                }

                // 3.5 Process and persist Parent Chunks in SQL Database
                var parentCache = new Dictionary<string, string>(); // Maps parentContent -> parentId Guid
                
                foreach (var chunk in chunks)
                {
                    if (!string.IsNullOrEmpty(chunk.ParentContent))
                    {
                        if (!parentCache.TryGetValue(chunk.ParentContent, out var parentId))
                        {
                            var parentChunk = new DbParentChunk
                            {
                                DocumentId = documentId,
                                Content = chunk.ParentContent
                            };
                            _dbContext.ParentChunks.Add(parentChunk);
                            parentCache[chunk.ParentContent] = parentChunk.Id;
                            parentId = parentChunk.Id;
                        }
                        
                        chunk.Metadata["parentId"] = parentId;
                    }
                }
                
                if (parentCache.Count > 0)
                {
                    Console.WriteLine($"[Document Ingestion] Persisting {parentCache.Count} Parent Chunks to SQL Database...");
                    await _dbContext.SaveChangesAsync().ConfigureAwait(false);
                }

                // 4. Generate Embeddings & Upsert to Pinecone
                if (chunks.Count > 0)
                {
                    Console.WriteLine($"[Document Ingestion] Generating dense vectors for new document chunks...");
                    var embeddingOptions = new EmbeddingGenerationOptions { Dimensions = 512 };

                    var vectorsToUpsert = EmbeddingsHelper.CreateVectors(
                        chunks,
                        idSelector: c => $"{documentId}_chunk_{c.ChunkIndex}",
                        contentSelector: c => c.Content,
                        apiKey: _openAiSettings.ApiKey,
                        options: embeddingOptions,
                        model: _openAiSettings.EmbeddingModel ?? "text-embedding-3-small",
                        metadataBuilder: c =>
                        {
                            var md = new Metadata();
                            md["chunkIndex"] = new MetadataValue(c.ChunkIndex.ToString());
                            md["pageNumber"] = new MetadataValue(c.PageNumber.ToString());
                            md["Content"] = new MetadataValue(c.Content);
                            
                            md["documentId"] = new MetadataValue(documentId);
                            md["sourceName"] = new MetadataValue(fileName);
                            md["sourceLink"] = new MetadataValue(filePath);

                            if (c.Metadata.TryGetValue("parentId", out var parentId))
                            {
                                md["parentId"] = new MetadataValue(parentId);
                            }

                            // Semantic metadata indexes
                            md["company"] = new MetadataValue(doc.Company ?? "Unknown");
                            md["documentType"] = new MetadataValue(doc.DocumentType ?? "Unknown");
                            md["version"] = new MetadataValue(doc.Version ?? "1.0");
                            md["isLatest"] = new MetadataValue(doc.IsLatest ? "true" : "false");
                            
                            return md;
                        });

                    var vectorsForUpsert = vectorsToUpsert.Select(v => new Vector
                    {
                        Id = v.Id,
                        Values = new ReadOnlyMemory<float>(v.Values),
                        Metadata = v.Metadata
                    }).ToList();

                    var upsertRequest = new UpsertRequest { Vectors = vectorsForUpsert };
                    Console.WriteLine($"[Document Ingestion] Upserting vectors to Pinecone index '{_pineconeSettings.IndexName}'...");
                    await index.UpsertAsync(upsertRequest).ConfigureAwait(false);
                }

                // 5. Complete Indexing
                doc.Status = "Indexed";
                _documentService.AddOrUpdate(doc);
                Console.WriteLine($"[Document Ingestion] Ingestion complete for '{fileName}' ({documentId}) with isLatest = {doc.IsLatest}.");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Document Ingestion Error] Failed to index document {documentId}: {ex}");
                doc.Status = "Failed";
                doc.ErrorMessage = ex.Message;
                _documentService.AddOrUpdate(doc);
            }
        }

        private List<TextChunk> ChunkStructuredDocument(StructuredDocument doc, int chunkSize = 3000, int overlap = 500)
        {
            var chunks = new List<TextChunk>();
            int globalChunkIndex = 0;

            foreach (var section in doc.Sections)
            {
                if (section is TableSection table)
                {
                    if (table.Rows.Count == 0) continue;

                    string fullTableMarkdown = table.ToMarkdown();
                    var headers = table.Headers;
                    string headersLine = "| " + string.Join(" | ", headers) + " |";
                    string sepLine = "| " + string.Join(" | ", headers.Select(_ => "---")) + " |";
                    
                    int rowsPerBlock = 5;
                    int overlapRows = 1;
                    int idx = 0;

                    while (idx < table.Rows.Count)
                    {
                        int count = Math.Min(rowsPerBlock, table.Rows.Count - idx);
                        var rowSubset = table.Rows.Skip(idx).Take(count).ToList();

                        var sbBlock = new System.Text.StringBuilder();
                        sbBlock.AppendLine(headersLine);
                        sbBlock.AppendLine(sepLine);
                        foreach (var r in rowSubset)
                        {
                            sbBlock.AppendLine("| " + string.Join(" | ", r) + " |");
                        }

                        chunks.Add(new TextChunk
                        {
                            ChunkIndex = globalChunkIndex++,
                            Content = sbBlock.ToString(),
                            PageNumber = table.PageOrSlideNumber,
                            ParentContent = fullTableMarkdown,
                            Metadata = new Dictionary<string, string>
                            {
                                { "PageNumber", table.PageOrSlideNumber.ToString() },
                                { "IsTableChild", "true" }
                            }
                        });

                        idx += (rowsPerBlock - overlapRows);
                        if (rowsPerBlock - overlapRows <= 0)
                        {
                            idx += rowsPerBlock;
                        }
                    }
                }
                else if (section is TextSection textSection)
                {
                    string text = textSection.ParagraphText;
                    if (string.IsNullOrWhiteSpace(text)) continue;

                    int index = 0;
                    while (index < text.Length)
                    {
                        int length = Math.Min(chunkSize, text.Length - index);
                        string chunkContent = text.Substring(index, length);

                        chunks.Add(new TextChunk
                        {
                            ChunkIndex = globalChunkIndex++,
                            Content = chunkContent,
                            PageNumber = section.PageOrSlideNumber,
                            Metadata = new Dictionary<string, string>
                            {
                                { "PageNumber", section.PageOrSlideNumber.ToString() }
                            }
                        });

                        int step = chunkSize - overlap;
                        if (step <= 0) step = chunkSize;
                        index += step;
                    }
                }
            }

            return chunks;
        }

        private async Task UpdatePineconeLatestMetadataAsync(IndexClient index, string docId, int chunkCount, bool isLatest)
        {
            try
            {
                var val = isLatest ? "true" : "false";
                var tasks = new List<Task>();
                using var semaphore = new SemaphoreSlim(10); // Run up to 10 parallel updates to prevent network choking

                for (int i = 0; i < chunkCount; i++)
                {
                    var vectorId = $"{docId}_chunk_{i}";
                    var updateRequest = new UpdateRequest
                    {
                        Id = vectorId,
                        SetMetadata = new Metadata { ["isLatest"] = new MetadataValue(val) }
                    };

                    tasks.Add(Task.Run(async () =>
                    {
                        await semaphore.WaitAsync().ConfigureAwait(false);
                        try
                        {
                            await index.UpdateAsync(updateRequest).ConfigureAwait(false);
                        }
                        finally
                        {
                            semaphore.Release();
                        }
                    }));
                }
                
                await Task.WhenAll(tasks).ConfigureAwait(false);
                Console.WriteLine($"[Document Ingestion] Successfully updated {chunkCount} vectors in Pinecone for {docId} to isLatest={val}.");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Document Ingestion Error] Failed to update Pinecone metadata for older document {docId}: {ex.Message}");
            }
        }

        // Fuzzy document matching helpers for grouping version families
        private bool IsSameDocumentFamily(UploadedDocument d1, UploadedDocument d2)
        {
            // If they have different companies extracted, they cannot be the same family
            if (!string.IsNullOrEmpty(d1.Company) && !string.IsNullOrEmpty(d2.Company))
            {
                if (!AreStringsSimilar(d1.Company, d2.Company))
                {
                    return false;
                }
            }

            // Compare Original Filenames fuzzy (excluding IDs and extensions) to ensure it's the exact same card/product
            var name1 = CleanFilenameForFuzzyMatch(d1.FileName);
            var name2 = CleanFilenameForFuzzyMatch(d2.FileName);
            return AreStringsSimilar(name1, name2);
        }

        private string CleanFilenameForFuzzyMatch(string filename)
        {
            var clean = Path.GetFileNameWithoutExtension(filename);
            // Strip Guid prefix if it exists in filename
            clean = Regex.Replace(clean, @"^[a-fA-F0-9]{32}_", "");
            return clean;
        }

        private bool AreStringsSimilar(string? s1, string? s2)
        {
            if (string.IsNullOrWhiteSpace(s1) && string.IsNullOrWhiteSpace(s2)) return true;
            if (string.IsNullOrWhiteSpace(s1) || string.IsNullOrWhiteSpace(s2)) return false;

            var norm1 = NormalizeMetadataString(s1);
            var norm2 = NormalizeMetadataString(s2);

            return norm1 == norm2 || norm1.Contains(norm2) || norm2.Contains(norm1);
        }

        private string NormalizeMetadataString(string s)
        {
            // lowercase and strip spaces + non-alphanumeric punctuation
            var norm = s.ToLowerInvariant();
            norm = Regex.Replace(norm, @"[^a-z0-9]", "");
            return norm;
        }

        private class UploadedDocumentVersionComparer : IComparer<UploadedDocument>
        {
            public int Compare(UploadedDocument? x, UploadedDocument? y)
            {
                if (x == null && y == null) return 0;
                if (x == null) return -1;
                if (y == null) return 1;

                // 1. Compare Fiscal Years (Descending)
                int yearX = x.FiscalYear ?? 0;
                int yearY = y.FiscalYear ?? 0;
                if (yearX != yearY)
                {
                    return yearX.CompareTo(yearY);
                }

                // 2. Compare Fiscal Quarters (Q4 > Q3 > Q2 > Q1 > N/A)
                int quarterX = GetQuarterRank(x.FiscalQuarter);
                int quarterY = GetQuarterRank(y.FiscalQuarter);
                if (quarterX != quarterY)
                {
                    return quarterX.CompareTo(quarterY);
                }

                // 3. Compare Semantic Versions (e.g. '2.5' > '1.0.3')
                int versionCompare = CompareVersions(x.Version, y.Version);
                if (versionCompare != 0)
                {
                    return versionCompare;
                }

                // 4. Compare Publication Dates
                int pubDateCompare = ComparePublicationDates(x.PublicationDate, y.PublicationDate);
                if (pubDateCompare != 0)
                {
                    return pubDateCompare;
                }

                // 5. Upload Timestamp (Fallback)
                return x.UploadedAt.CompareTo(y.UploadedAt);
            }

            private int GetQuarterRank(string? q)
            {
                if (string.IsNullOrWhiteSpace(q)) return 0;
                var upper = q.ToUpperInvariant();
                if (upper.Contains("Q4")) return 4;
                if (upper.Contains("Q3")) return 3;
                if (upper.Contains("Q2")) return 2;
                if (upper.Contains("Q1")) return 1;
                return 0;
            }

            private int CompareVersions(string? v1, string? v2)
            {
                if (string.IsNullOrWhiteSpace(v1) && string.IsNullOrWhiteSpace(v2)) return 0;
                if (string.IsNullOrWhiteSpace(v1)) return -1;
                if (string.IsNullOrWhiteSpace(v2)) return 1;

                var parts1 = v1.Split('.');
                var parts2 = v2.Split('.');

                for (int i = 0; i < Math.Max(parts1.Length, parts2.Length); i++)
                {
                    int p1 = 0;
                    if (i < parts1.Length) int.TryParse(parts1[i], out p1);
                    int p2 = 0;
                    if (i < parts2.Length) int.TryParse(parts2[i], out p2);

                    if (p1 != p2)
                    {
                        return p1.CompareTo(p2);
                    }
                }
                return 0;
            }

            private int ComparePublicationDates(string? d1, string? d2)
            {
                bool parsed1 = DateTime.TryParse(d1, out var dt1);
                bool parsed2 = DateTime.TryParse(d2, out var dt2);

                if (parsed1 && parsed2) return dt1.CompareTo(dt2);
                if (parsed1) return 1;
                if (parsed2) return -1;

                return string.Compare(d1, d2, StringComparison.OrdinalIgnoreCase);
            }
        }
    }
}