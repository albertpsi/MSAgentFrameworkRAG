using System;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using MSAgentFrameworkRAG.Interfaces;

namespace MSAgentFrameworkRAG.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class DocumentsController : ControllerBase
    {
        private readonly IDocumentService _documentService;
        private readonly StorageSettings _storageSettings;
        private readonly ParserSettings _parserSettings;
        private readonly IHttpClientFactory _httpClientFactory;

        public DocumentsController(
            IDocumentService documentService,
            IOptions<StorageSettings> storageOptions,
            IOptions<ParserSettings> parserOptions,
            IHttpClientFactory httpClientFactory)
        {
            _documentService = documentService ?? throw new ArgumentNullException(nameof(documentService));
            _storageSettings = storageOptions?.Value ?? throw new ArgumentNullException(nameof(storageOptions));
            _parserSettings = parserOptions?.Value ?? throw new ArgumentNullException(nameof(parserOptions));
            _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));

            // Ensure storage directories exist
            if (!Directory.Exists(_storageSettings.DocumentsDirectory))
            {
                Directory.CreateDirectory(_storageSettings.DocumentsDirectory);
            }
            if (!Directory.Exists(_storageSettings.ParsedDirectory))
            {
                Directory.CreateDirectory(_storageSettings.ParsedDirectory);
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
            
            // Save as {documentId}{extension} in the shared documents folder
            var filePath = Path.Combine(_storageSettings.DocumentsDirectory, $"{documentId}{extension}");

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

            // Trigger the Python Docling worker asynchronously (fire-and-forget)
            var workerUrl = $"{_parserSettings.DoclingWorkerUrl.TrimEnd('/')}/api/parse";
            var parsePayload = new
            {
                documentId = documentId,
                fileName = fileName,
                extension = extension
            };

            _ = Task.Run(async () =>
            {
                try
                {
                    using var client = _httpClientFactory.CreateClient();
                    var content = new StringContent(
                        JsonSerializer.Serialize(parsePayload),
                        Encoding.UTF8,
                        "application/json"
                    );

                    var response = await client.PostAsync(workerUrl, content).ConfigureAwait(false);
                    if (!response.IsSuccessStatusCode)
                    {
                        Console.WriteLine($"[Documents Controller ERROR] Python worker returned status: {response.StatusCode}");
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[Documents Controller ERROR] Failed to contact Python worker at {workerUrl}: {ex.Message}");
                }
            });

            return Ok(doc);
        }
    }
}
