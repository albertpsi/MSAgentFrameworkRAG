using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using MSAgentFrameworkRAG.Interfaces;
using Quartz;

namespace MSAgentFrameworkRAG.Jobs
{
    [DisallowConcurrentExecution]
    public class IngestionPollingJob : IJob
    {
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly StorageSettings _storageSettings;

        public IngestionPollingJob(IServiceScopeFactory scopeFactory, IOptions<StorageSettings> storageOptions)
        {
            _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
            _storageSettings = storageOptions?.Value ?? throw new ArgumentNullException(nameof(storageOptions));
        }

        public async Task Execute(IJobExecutionContext context)
        {
            using var scope = _scopeFactory.CreateScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var ingestionService = scope.ServiceProvider.GetRequiredService<IDocumentIngestionService>();

            // Query for documents that are in "Pending" state
            var pendingDocs = await dbContext.UploadedDocuments
                .Where(d => d.Status == "Pending")
                .ToListAsync(context.CancellationToken)
                .ConfigureAwait(false);

            if (!pendingDocs.Any())
            {
                return;
            }

            Console.WriteLine($"[Ingestion Polling Job] Found {pendingDocs.Count} pending documents. Checking for parsed JSON outputs...");

            foreach (var doc in pendingDocs)
            {
                var parsedJsonPath = Path.Combine(_storageSettings.ParsedDirectory, $"{doc.Id}.json");
                
                if (File.Exists(parsedJsonPath))
                {
                    Console.WriteLine($"[Ingestion Polling Job] Found parsed output for document '{doc.FileName}' ({doc.Id}). Starting full ingestion...");
                    
                    // Determine file extension
                    var ext = Path.GetExtension(doc.FileName).ToLowerInvariant();
                    var originalDocPath = Path.Combine(_storageSettings.DocumentsDirectory, $"{doc.Id}{ext}");

                    try
                    {
                        // IngestDocumentAsync handles database status transitions internally (e.g. Processing -> Indexed)
                        await ingestionService.IngestDocumentAsync(doc.Id, originalDocPath, doc.FileName).ConfigureAwait(false);
                        Console.WriteLine($"[Ingestion Polling Job] Ingestion successfully completed for document '{doc.FileName}' ({doc.Id}).");
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[Ingestion Polling Job ERROR] Ingestion failed for document '{doc.FileName}' ({doc.Id}): {ex.Message}");
                        doc.Status = "Failed";
                        doc.ErrorMessage = ex.Message;
                        dbContext.Entry(doc).State = EntityState.Modified;
                        await dbContext.SaveChangesAsync(context.CancellationToken).ConfigureAwait(false);
                    }
                }
            }
        }
    }
}
