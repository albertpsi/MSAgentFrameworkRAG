using System;
using System.IO;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Quartz;
using MSAgentFrameworkRAG.Interfaces;

namespace MSAgentFrameworkRAG.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class DocumentsController : ControllerBase
    {
        private readonly IDocumentService _documentService;
        private readonly ISchedulerFactory _schedulerFactory;
        private readonly OpenAISettings _openAiSettings;
        private readonly PineconeSettings _pineconeSettings;
        private readonly string _uploadsDir;

        public DocumentsController(
            IDocumentService documentService,
            ISchedulerFactory schedulerFactory,
            IOptions<OpenAISettings> openAiOptions,
            IOptions<PineconeSettings> pineconeOptions)
        {
            _documentService = documentService ?? throw new ArgumentNullException(nameof(documentService));
            _schedulerFactory = schedulerFactory ?? throw new ArgumentNullException(nameof(schedulerFactory));
            _openAiSettings = openAiOptions?.Value ?? throw new ArgumentNullException(nameof(openAiOptions));
            _pineconeSettings = pineconeOptions?.Value ?? throw new ArgumentNullException(nameof(pineconeOptions));

            // Set up local upload path
            _uploadsDir = Path.Combine(Directory.GetCurrentDirectory(), "wwwroot", "uploads");
            if (!Directory.Exists(_uploadsDir))
            {
                Directory.CreateDirectory(_uploadsDir);
            }
        }

        // GET /api/documents
        [HttpGet]
        public IActionResult GetDocuments()
        {
            return Ok(_documentService.GetAll());
        }

        // POST /api/upload
        [HttpPost("/api/upload")]
        [DisableRequestSizeLimit]
        public async Task<IActionResult> UploadFile(IFormFile file)
        {
            if (file == null || file.Length == 0)
            {
                return BadRequest("No file uploaded.");
            }

            var documentId = Guid.NewGuid().ToString("N");
            var fileName = Path.GetFileName(file.FileName);
            var extension = Path.GetExtension(fileName).ToLowerInvariant();
            if (extension != ".pdf" && extension != ".docx")
            {
                return BadRequest("Only PDF and DOCX contract documents are supported.");
            }
            
            // Clean filename to remove any parentheses to prevent MSBuild compilation issues inside wwwroot folder
            var cleanFileName = fileName.Replace("(", "").Replace(")", "").Replace(" ", "_");
            var safeFileName = $"{documentId}_{cleanFileName}";
            var filePath = Path.Combine(_uploadsDir, safeFileName);

            using (var stream = new FileStream(filePath, FileMode.Create))
            {
                await file.CopyToAsync(stream);
            }

            var doc = new UploadedDocument
            {
                Id = documentId,
                FileName = fileName,
                Status = "Pending",
                UploadedAt = DateTime.UtcNow
            };
            _documentService.AddOrUpdate(doc);

            // Schedule Quartz Job for background processing (reading, chunking, metadata extraction, Pinecone indexing)
            var scheduler = await _schedulerFactory.GetScheduler();
            
            var job = JobBuilder.Create<FileIngestionJob>()
                .WithIdentity($"Job_{documentId}", "IngestionGroup")
                .UsingJobData("documentId", documentId)
                .UsingJobData("filePath", filePath)
                .UsingJobData("fileName", fileName)
                .UsingJobData("openAIApiKey", _openAiSettings.ApiKey)
                .UsingJobData("pineconeApiKey", _pineconeSettings.ApiKey)
                .UsingJobData("indexName", _pineconeSettings.IndexName)
                .UsingJobData("embeddingModel", _openAiSettings.EmbeddingModel)
                .Build();

            var trigger = TriggerBuilder.Create()
                .WithIdentity($"Trigger_{documentId}", "IngestionGroup")
                .StartNow()
                .Build();

            await scheduler.ScheduleJob(job, trigger);

            return Ok(doc);
        }
    }
}
