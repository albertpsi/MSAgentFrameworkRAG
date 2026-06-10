using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.EntityFrameworkCore;
using System.Threading.Tasks;

namespace MSAgentFrameworkRAG
{
    public static class LocalBm25Retriever
    {
        private static readonly HashSet<string> Stopwords = new(StringComparer.OrdinalIgnoreCase)
        {
            "a", "about", "above", "after", "again", "against", "all", "am", "an", "and", "any", "are", "arent", "as", "at",
            "be", "because", "been", "before", "being", "below", "between", "both", "but", "by", "can", "cannot", "could",
            "did", "do", "does", "doing", "down", "during", "each", "few", "for", "from", "further", "had", "has", "have",
            "having", "he", "her", "here", "hers", "herself", "him", "himself", "his", "how", "i", "if", "in", "into", "is",
            "it", "its", "itself", "me", "more", "most", "my", "myself", "no", "nor", "not", "of", "off", "on", "once", "only",
            "or", "other", "ought", "our", "ours", "ourselves", "out", "over", "own", "same", "she", "should", "so", "some",
            "such", "than", "that", "the", "their", "theirs", "them", "themselves", "then", "there", "these", "they", "this",
            "those", "through", "to", "too", "under", "until", "up", "very", "was", "we", "were", "what", "when", "where",
            "which", "while", "who", "whom", "why", "with", "would", "you", "your", "yours", "yourself", "yourselves"
        };

        public static async Task<List<SourceCitation>> RetrieveBm25CandidatesAsync(
            AppDbContext dbContext,
            string query,
            List<string>? documentIds,
            int topN = 15,
            double k1 = 1.2,
            double b = 0.75)
        {
            if (string.IsNullOrWhiteSpace(query))
            {
                return new List<SourceCitation>();
            }

            // 1. Tokenize query and filter keywords
            var keywords = query
                .Split(new[] { ' ', ',', '.', ';', '?', '!', '-', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(k => k.Trim().ToLowerInvariant())
                .Where(k => k.Length > 2 && !Stopwords.Contains(k))
                .Distinct()
                .ToList();

            if (keywords.Count == 0)
            {
                return new List<SourceCitation>();
            }

            // 2. Fetch baseline matching chunks from database
            // Build base query and apply optional document ID filter first
            IQueryable<DbParentChunk> chunkQuery = dbContext.ParentChunks.AsNoTracking();
            if (documentIds != null && documentIds.Count > 0)
            {
                chunkQuery = chunkQuery.Where(c => documentIds.Contains(c.DocumentId));
            }

            // Filter to chunks that match any keyword using EF.Functions.Like
            // Use string concatenation for the LIKE pattern (EF Core cannot translate string.Format or interpolated strings here)
            chunkQuery = chunkQuery.Where(c => keywords.Any(kw => EF.Functions.Like(c.Content, "%" + kw + "%")));

            var matchingChunks = await chunkQuery.ToListAsync().ConfigureAwait(false);

            if (!matchingChunks.Any())
            {
                return new List<SourceCitation>();
            }

            // 3. Compute Corpus Statistics
            int totalDocuments = await dbContext.ParentChunks.CountAsync();
            
            // Calculate word lengths for all matching documents
            var docLengths = matchingChunks.ToDictionary(
                c => c.Id,
                c => c.Content.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries).Length
            );
            
            // Approximate average document length across the system
            double avgdl = docLengths.Values.Any() ? docLengths.Values.Average() : 250.0;

            // Compute IDF for each keyword
            var idfMap = new Dictionary<string, double>();
            foreach (var kw in keywords)
            {
                int docFreq = matchingChunks.Count(c => c.Content.Contains(kw, StringComparison.OrdinalIgnoreCase));
                double idf = Math.Log((totalDocuments - docFreq + 0.5) / (docFreq + 0.5) + 1.0);
                // Lower-bound IDF to prevent negative values for extremely common words
                idfMap[kw] = Math.Max(0.0001, idf);
            }

            // 4. Score each chunk using the BM25 formula
            var scoredCandidates = new List<(DbParentChunk Chunk, double Score)>();

            foreach (var chunk in matchingChunks)
            {
                double score = 0.0;
                var text = chunk.Content;
                int docLen = docLengths[chunk.Id];

                foreach (var kw in keywords)
                {
                    // Compute Term Frequency (TF)
                    int tf = 0;
                    int index = 0;
                    while ((index = text.IndexOf(kw, index, StringComparison.OrdinalIgnoreCase)) != -1)
                    {
                        tf++;
                        index += kw.Length;
                    }

                    if (tf > 0)
                    {
                        // BM25 scoring formula for term kw
                        double numerator = tf * (k1 + 1);
                        double denominator = tf + k1 * (1.0 - b + b * ((double)docLen / avgdl));
                        score += idfMap[kw] * (numerator / denominator);
                    }
                }

                scoredCandidates.Add((chunk, score));
            }

            // Fetch document metadata for formatting source citations
            var uniqueDocIds = scoredCandidates.Select(s => s.Chunk.DocumentId).Distinct().ToList();
            var docMetadata = await dbContext.UploadedDocuments
                .AsNoTracking()
                .Where(d => uniqueDocIds.Contains(d.Id))
                .ToDictionaryAsync(d => d.Id, d => d.FileName);

            // 5. Select and format Top N candidates
            return scoredCandidates
                .OrderByDescending(s => s.Score)
                .Take(topN)
                .Select(s => new SourceCitation
                {
                    SourceName = docMetadata.TryGetValue(s.Chunk.DocumentId, out var fName) ? fName : "Database contract chunk",
                    SourceLink = "",
                    Text = s.Chunk.Content,
                    Score = s.Score
                })
                .ToList();
        }
    }
}
