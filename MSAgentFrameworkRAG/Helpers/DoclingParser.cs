using System;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Options;
using MSAgentFrameworkRAG.Interfaces;

namespace MSAgentFrameworkRAG.Helpers
{
    public class DoclingParser : IDocumentParser
    {
        private readonly StorageSettings _storageSettings;
        private static readonly JsonSerializerOptions _jsonOptions = new()
        {
            PropertyNameCaseInsensitive = true
        };

        public DoclingParser(IOptions<StorageSettings> storageOptions)
        {
            _storageSettings = storageOptions?.Value ?? throw new ArgumentNullException(nameof(storageOptions));
        }

        public async Task<ParsedDocument> ParseAsync(string documentId, string filePath, CancellationToken cancellationToken = default)
        {
            var parsedFilePath = Path.Combine(_storageSettings.ParsedDirectory, $"{documentId}.json");
            
            if (!File.Exists(parsedFilePath))
            {
                throw new FileNotFoundException($"Parsed JSON document not found for ID: {documentId}", parsedFilePath);
            }

            using var stream = File.OpenRead(parsedFilePath);
            var parsedDoc = await JsonSerializer.DeserializeAsync<ParsedDocument>(stream, _jsonOptions, cancellationToken);
            
            return parsedDoc ?? throw new InvalidOperationException($"Failed to deserialize parsed JSON document from path: {parsedFilePath}");
        }
    }
}
