using System;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
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
        private readonly IServiceScopeFactory _scopeFactory;

        public DocumentsController(
            IDocumentService documentService,
            IOptions<StorageSettings> storageOptions,
            IOptions<ParserSettings> parserOptions,
            IHttpClientFactory httpClientFactory,
            IServiceScopeFactory scopeFactory)
        {
            _documentService = documentService ?? throw new ArgumentNullException(nameof(documentService));
            _storageSettings = storageOptions?.Value ?? throw new ArgumentNullException(nameof(storageOptions));
            _parserSettings = parserOptions?.Value ?? throw new ArgumentNullException(nameof(parserOptions));
            _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
            _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));

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

            // Trigger the remote Google Colab GPU worker synchronously via Multipart Form Data
            var workerUrl = $"{_parserSettings.DoclingWorkerUrl.TrimEnd('/')}/parse";

            _ = Task.Run(async () =>
            {
                try
                {
                    Console.WriteLine($"[Documents Controller] Uploading '{fileName}' to Colab GPU at {workerUrl}...");
                    using var client = _httpClientFactory.CreateClient();
                    client.Timeout = TimeSpan.FromMinutes(10); // Bounded timeout for large documents
                    client.DefaultRequestHeaders.Add("ngrok-skip-browser-warning", "true"); // Bypass ngrok warning page

                    using var form = new MultipartFormDataContent();
                    using var fileStream = System.IO.File.OpenRead(filePath);
                    using var streamContent = new StreamContent(fileStream);
                    
                    string mimeType = extension == ".pdf" ? "application/pdf" : "application/vnd.openxmlformats-officedocument.wordprocessingml.document";
                    streamContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(mimeType);
                    form.Add(streamContent, "file", fileName);

                    var response = await client.PostAsync(workerUrl, form).ConfigureAwait(false);
                    if (response.IsSuccessStatusCode)
                    {
                        var jsonString = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                        var parsedFilePath = Path.Combine(_storageSettings.ParsedDirectory, $"{documentId}.json");
                        await System.IO.File.WriteAllTextAsync(parsedFilePath, jsonString).ConfigureAwait(false);
                        Console.WriteLine($"[Documents Controller SUCCESS] Document '{fileName}' ({documentId}) parsed and stored.");
                    }
                    else
                    {
                        var errorMsg = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                        Console.WriteLine($"[Documents Controller ERROR] Colab worker returned status {response.StatusCode}: {errorMsg}");
                        
                        // Mark document as failed using a new service scope
                        using var scope = _scopeFactory.CreateScope();
                        var scopedDocService = scope.ServiceProvider.GetRequiredService<IDocumentService>();
                        var scopedDoc = scopedDocService.Get(documentId);
                        if (scopedDoc != null)
                        {
                            scopedDoc.Status = "Failed";
                            scopedDoc.ErrorMessage = $"Colab GPU parsing failed: {response.StatusCode} - {errorMsg}";
                            scopedDocService.AddOrUpdate(scopedDoc);
                        }
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[Documents Controller ERROR] Failed to parse document {documentId} on Google Colab: {ex.Message}");
                    
                    using var scope = _scopeFactory.CreateScope();
                    var scopedDocService = scope.ServiceProvider.GetRequiredService<IDocumentService>();
                    var scopedDoc = scopedDocService.Get(documentId);
                    if (scopedDoc != null)
                    {
                        scopedDoc.Status = "Failed";
                        scopedDoc.ErrorMessage = ex.Message;
                        scopedDocService.AddOrUpdate(scopedDoc);
                    }
                }
            });

            return Ok(doc);
        }
    }
}
