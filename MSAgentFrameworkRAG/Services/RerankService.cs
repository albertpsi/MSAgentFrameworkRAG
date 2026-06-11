using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Pinecone;
using MSAgentFrameworkRAG.Interfaces;

namespace MSAgentFrameworkRAG.Services
{
    public class RerankService : IRerankService
    {
        private readonly PineconeSettings _pineconeSettings;
        private readonly ILogger<RerankService> _logger;

        public RerankService(
            IOptions<PineconeSettings> pineconeOptions,
            ILogger<RerankService> logger)
        {
            _pineconeSettings = pineconeOptions?.Value ?? throw new ArgumentNullException(nameof(pineconeOptions));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public async Task<List<SourceCitation>> RerankAsync(string query, List<SourceCitation> candidates, CancellationToken cancellationToken = default)
        {
            if (candidates == null || !candidates.Any())
            {
                _logger.LogInformation("[Rerank Service] No candidates supplied for reranking.");
                return new List<SourceCitation>();
            }

            _logger.LogInformation(
                "[Rerank Service] Starting native Pinecone SDK reranking for query '{Query}' over {Count} candidate chunks...", 
                query, candidates.Count);

            var stopwatch = Stopwatch.StartNew();

            try
            {
                // 1. Initialize native Pinecone client
                var pinecone = new PineconeClient(_pineconeSettings.ApiKey);

                // 2. Build type-safe RerankRequest using configuration variables
                var rerankRequest = new RerankRequest
                {
                    Model = _pineconeSettings.RerankModel ?? "bge-reranker-v2-m3",
                    Query = query,
                    TopN = _pineconeSettings.RerankTopN,
                    ReturnDocuments = true, // Set to false to save bandwidth (we map back to candidates locally)
                    Documents = candidates.Select((c, idx) => new Dictionary<string, object?>
                    {
                        { "id", idx.ToString() },
                        { "text", c.Text }
                    }).ToList(),
                    Parameters = new Dictionary<string, object?>
                    {
                        { "truncate", "END" }
                    }
                };

                // 3. Invoke native SDK Rerank
                var response = await pinecone.Inference.RerankAsync(rerankRequest, cancellationToken: cancellationToken).ConfigureAwait(false);
                stopwatch.Stop();
                var elapsedMs = stopwatch.Elapsed.TotalMilliseconds;

                _logger.LogInformation(
                    "[Rerank Service] Rerank completed in {ElapsedMs:F2}ms. Model used: '{Model}'", 
                    elapsedMs, response.Model);

                // 4. Map, score, and collect results (no threshold filtering)
                var finalCitations = new List<SourceCitation>();

                foreach (var rankedDoc in response.Data)
                {
                    var originalCandidate = candidates[rankedDoc.Index];
                    originalCandidate.RerankScore = rankedDoc.Score;
                    originalCandidate.QueryTimeMs = elapsedMs;
                    
                    finalCitations.Add(originalCandidate);
                }

                _logger.LogInformation(
                    "[Rerank Service] Returning all {Count} ranked chunks from the reranker (no score threshold applied).", 
                    finalCitations.Count);

                return finalCitations;
            }
            catch (Exception ex)
            {
                stopwatch.Stop();
                _logger.LogError(ex, "[Rerank Service Error] Failed to perform native Pinecone Reranking.");
                
                // Fail-safe fallback: if reranker is down, return top candidates without reranking, but flag it
                foreach (var candidate in candidates.Take(_pineconeSettings.RerankTopN))
                {
                    candidate.QueryTimeMs = stopwatch.Elapsed.TotalMilliseconds;
                }
                return candidates.Take(_pineconeSettings.RerankTopN).ToList();
            }
        }
    }
}
