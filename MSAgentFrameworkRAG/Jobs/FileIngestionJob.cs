using System;
using System.Threading.Tasks;
using Quartz;
using MSAgentFrameworkRAG.Interfaces;

namespace MSAgentFrameworkRAG
{
    [DisallowConcurrentExecution]
    public class FileIngestionJob : IJob
    {
        private readonly IDocumentIngestionService _ingestionService;

        public FileIngestionJob(IDocumentIngestionService ingestionService)
        {
            _ingestionService = ingestionService ?? throw new ArgumentNullException(nameof(ingestionService));
        }

        public async Task Execute(IJobExecutionContext context)
        {
            var dataMap = context.MergedJobDataMap;
            var documentId = dataMap.GetString("documentId") ?? string.Empty;
            var filePath = dataMap.GetString("filePath") ?? string.Empty;
            var fileName = dataMap.GetString("fileName") ?? string.Empty;

            Console.WriteLine($"[File Ingestion Job] Background execution started for '{fileName}' ({documentId})...");

            try
            {
                await _ingestionService.IngestDocumentAsync(documentId, filePath, fileName).ConfigureAwait(false);
                Console.WriteLine($"[File Ingestion Job] Background execution completed for '{fileName}' ({documentId}).");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[File Ingestion Job ERROR] Execution failed: {ex}");
            }
        }
    }
}
