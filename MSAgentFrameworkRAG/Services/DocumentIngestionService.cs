using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Text.Json;
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

                Console.WriteLine("[Document Ingestion] Running contract metadata extraction agent...");
                var metadata = await _metadataService.ExtractMetadataAsync(filePath, fileName).ConfigureAwait(false);
                var result = metadata?.Result ?? new DocumentMetadataResult { FileName = fileName };

                doc.FileName = UseDefault(result.FileName, fileName);
                doc.PartyA = UseDefault(result.PartyA);
                doc.PartyB = UseDefault(result.PartyB);
                doc.AgreementTitle = UseDefault(result.AgreementTitle);
                doc.AgreementType = UseDefault(result.AgreementType, "Other Agreement");
                doc.EffectiveDate = UseDefault(result.EffectiveDate);
                doc.ExecutionDate = UseDefault(result.ExecutionDate);
                doc.ExpirationDate = UseDefault(result.ExpirationDate);
                doc.GoverningLaw = UseDefault(result.GoverningLaw);
                doc.Jurisdiction = UseDefault(result.Jurisdiction);
                doc.ContractStatus = UseDefault(result.ContractStatus, "unknown").ToLowerInvariant();
                doc.AmendmentNumber = UseDefault(result.AmendmentNumber);
                doc.SupersedesDocument = UseDefault(result.SupersedesDocument);
                doc.Version = UseDefault(result.Version, "1.0");
                doc.ContractMetadataJson = JsonSerializer.Serialize(result);

                Console.WriteLine($"[Document Ingestion] Contract metadata parsed: PartyA={doc.PartyA}, PartyB={doc.PartyB}, Type={doc.AgreementType}, EffectiveDate={doc.EffectiveDate}, Version={doc.Version}");

                Console.WriteLine("[Document Ingestion] Parsing and chunking contract document...");
                var parser = Helpers.ParserFactory.GetParser(filePath);
                var structuredDoc = parser.Parse(filePath);
                var chunks = ChunkStructuredDocument(structuredDoc, chunkSize: 3000, overlap: 500);
                doc.ChunkCount = chunks.Count;

                Console.WriteLine($"[Document Ingestion] Split document into {doc.ChunkCount} chunks.");

                Console.WriteLine("[Document Ingestion] Processing contract version control...");
                var allDocs = _documentService.GetAll();
                var familyDocs = allDocs
                    .Where(d => d.Id != documentId && d.Status != "Failed" && IsSameDocumentFamily(d, doc))
                    .ToList();

                familyDocs.Add(doc);

                var sortedDocs = familyDocs.OrderByDescending(d => d, new UploadedDocumentVersionComparer()).ToList();

                var pinecone = new PineconeClient(_pineconeSettings.ApiKey);
                var index = pinecone.Index(_pineconeSettings.IndexName);

                for (int i = 0; i < sortedDocs.Count; i++)
                {
                    var d = sortedDocs[i];
                    bool shouldBeLatest = i == 0;

                    if (d.Id == documentId)
                    {
                        doc.IsLatest = shouldBeLatest;
                    }
                    else if (d.IsLatest != shouldBeLatest)
                    {
                        Console.WriteLine($"[Document Ingestion] Version shift: document '{d.FileName}' ({d.Id}) IsLatest changed from {d.IsLatest} to {shouldBeLatest}. Updating vectors in Pinecone...");
                        d.IsLatest = shouldBeLatest;
                        _documentService.AddOrUpdate(d);

                        if (d.Status == "Indexed")
                        {
                            await UpdatePineconeLatestMetadataAsync(index, d.Id, d.ChunkCount, shouldBeLatest).ConfigureAwait(false);
                        }
                    }
                }

                var parentCache = new Dictionary<string, string>();
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
                    Console.WriteLine($"[Document Ingestion] Persisting {parentCache.Count} parent chunks to SQL Database...");
                    await _dbContext.SaveChangesAsync().ConfigureAwait(false);
                }

                if (chunks.Count > 0)
                {
                    Console.WriteLine("[Document Ingestion] Generating dense vectors for new document chunks...");
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
                            md["sourceName"] = new MetadataValue(doc.FileName ?? fileName);
                            md["sourceLink"] = new MetadataValue(filePath);

                            if (c.Metadata.TryGetValue("parentId", out var parentId))
                            {
                                md["parentId"] = new MetadataValue(parentId);
                            }

                            md["partyA"] = new MetadataValue(doc.PartyA ?? "Unknown");
                            md["partyB"] = new MetadataValue(doc.PartyB ?? "Unknown");
                            md["agreementTitle"] = new MetadataValue(doc.AgreementTitle ?? "Unknown");
                            md["agreementType"] = new MetadataValue(doc.AgreementType ?? "Other Agreement");
                            md["effectiveDate"] = new MetadataValue(doc.EffectiveDate ?? "Unknown");
                            md["executionDate"] = new MetadataValue(doc.ExecutionDate ?? "Unknown");
                            md["expirationDate"] = new MetadataValue(doc.ExpirationDate ?? "Unknown");
                            md["governingLaw"] = new MetadataValue(doc.GoverningLaw ?? "Unknown");
                            md["jurisdiction"] = new MetadataValue(doc.Jurisdiction ?? "Unknown");
                            md["contractStatus"] = new MetadataValue(doc.ContractStatus ?? "unknown");
                            md["amendmentNumber"] = new MetadataValue(doc.AmendmentNumber ?? "Unknown");
                            md["supersedesDocument"] = new MetadataValue(doc.SupersedesDocument ?? "Unknown");
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

        private static string UseDefault(string? value, string fallback = "Unknown")
        {
            return string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
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

                        idx += rowsPerBlock - overlapRows;
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
                using var semaphore = new SemaphoreSlim(10);

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

        private bool IsSameDocumentFamily(UploadedDocument d1, UploadedDocument d2)
        {
            if (ReferencesDocument(d1.SupersedesDocument, d2) || ReferencesDocument(d2.SupersedesDocument, d1))
            {
                return true;
            }

            if (!AreStringsSimilar(d1.AgreementType, d2.AgreementType))
            {
                return false;
            }

            if (!HaveSamePartySet(d1, d2))
            {
                return false;
            }

            var title1 = UseDefault(d1.AgreementTitle, CleanFilenameForFuzzyMatch(d1.FileName));
            var title2 = UseDefault(d2.AgreementTitle, CleanFilenameForFuzzyMatch(d2.FileName));

            if (!IsUnknown(title1) && !IsUnknown(title2))
            {
                return AreStringsSimilar(title1, title2);
            }

            return AreStringsSimilar(CleanFilenameForFuzzyMatch(d1.FileName), CleanFilenameForFuzzyMatch(d2.FileName));
        }

        private bool ReferencesDocument(string? reference, UploadedDocument target)
        {
            if (IsUnknown(reference)) return false;

            return AreStringsSimilar(reference, target.AgreementTitle)
                || AreStringsSimilar(reference, target.FileName);
        }

        private bool HaveSamePartySet(UploadedDocument d1, UploadedDocument d2)
        {
            var d1A = d1.PartyA;
            var d1B = d1.PartyB;
            var d2A = d2.PartyA;
            var d2B = d2.PartyB;

            if (IsUnknown(d1A) || IsUnknown(d1B) || IsUnknown(d2A) || IsUnknown(d2B))
            {
                return AreStringsSimilar(d1A, d2A)
                    || AreStringsSimilar(d1A, d2B)
                    || AreStringsSimilar(d1B, d2A)
                    || AreStringsSimilar(d1B, d2B);
            }

            return (AreStringsSimilar(d1A, d2A) && AreStringsSimilar(d1B, d2B))
                || (AreStringsSimilar(d1A, d2B) && AreStringsSimilar(d1B, d2A));
        }

        private string CleanFilenameForFuzzyMatch(string filename)
        {
            var clean = Path.GetFileNameWithoutExtension(filename);
            clean = Regex.Replace(clean, @"^[a-fA-F0-9]{32}_", "");
            return clean;
        }

        private bool AreStringsSimilar(string? s1, string? s2)
        {
            if (IsUnknown(s1) && IsUnknown(s2)) return true;
            if (IsUnknown(s1) || IsUnknown(s2)) return false;

            var norm1 = NormalizeMetadataString(s1!);
            var norm2 = NormalizeMetadataString(s2!);

            return norm1 == norm2 || norm1.Contains(norm2) || norm2.Contains(norm1);
        }

        private static bool IsUnknown(string? value)
        {
            return string.IsNullOrWhiteSpace(value)
                || value.Equals("Unknown", StringComparison.OrdinalIgnoreCase)
                || value.Equals("N/A", StringComparison.OrdinalIgnoreCase);
        }

        private string NormalizeMetadataString(string s)
        {
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

                int amendmentCompare = CompareAmendments(x.AmendmentNumber, y.AmendmentNumber);
                if (amendmentCompare != 0)
                {
                    return amendmentCompare;
                }

                int effectiveDateCompare = CompareContractDates(x.EffectiveDate, y.EffectiveDate);
                if (effectiveDateCompare != 0)
                {
                    return effectiveDateCompare;
                }

                int executionDateCompare = CompareContractDates(x.ExecutionDate, y.ExecutionDate);
                if (executionDateCompare != 0)
                {
                    return executionDateCompare;
                }

                int versionCompare = CompareVersions(x.Version, y.Version);
                if (versionCompare != 0)
                {
                    return versionCompare;
                }

                return x.UploadedAt.CompareTo(y.UploadedAt);
            }

            private int CompareAmendments(string? a1, string? a2)
            {
                int n1 = ParseAmendmentNumber(a1);
                int n2 = ParseAmendmentNumber(a2);
                return n1.CompareTo(n2);
            }

            private int ParseAmendmentNumber(string? amendment)
            {
                if (string.IsNullOrWhiteSpace(amendment) || amendment.Equals("Unknown", StringComparison.OrdinalIgnoreCase))
                {
                    return 0;
                }

                var lower = amendment.ToLowerInvariant();
                var wordNumbers = new Dictionary<string, int>
                {
                    ["first"] = 1,
                    ["second"] = 2,
                    ["third"] = 3,
                    ["fourth"] = 4,
                    ["fifth"] = 5,
                    ["sixth"] = 6,
                    ["seventh"] = 7,
                    ["eighth"] = 8,
                    ["ninth"] = 9,
                    ["tenth"] = 10
                };

                foreach (var pair in wordNumbers)
                {
                    if (lower.Contains(pair.Key))
                    {
                        return pair.Value;
                    }
                }

                var match = Regex.Match(amendment, @"\d+");
                return match.Success && int.TryParse(match.Value, out var value) ? value : 0;
            }

            private int CompareVersions(string? v1, string? v2)
            {
                if (string.IsNullOrWhiteSpace(v1) && string.IsNullOrWhiteSpace(v2)) return 0;
                if (string.IsNullOrWhiteSpace(v1)) return -1;
                if (string.IsNullOrWhiteSpace(v2)) return 1;

                var parts1 = Regex.Matches(v1, @"\d+").Select(m => int.Parse(m.Value)).ToArray();
                var parts2 = Regex.Matches(v2, @"\d+").Select(m => int.Parse(m.Value)).ToArray();

                for (int i = 0; i < Math.Max(parts1.Length, parts2.Length); i++)
                {
                    int p1 = i < parts1.Length ? parts1[i] : 0;
                    int p2 = i < parts2.Length ? parts2[i] : 0;

                    if (p1 != p2)
                    {
                        return p1.CompareTo(p2);
                    }
                }
                return 0;
            }

            private int CompareContractDates(string? d1, string? d2)
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
