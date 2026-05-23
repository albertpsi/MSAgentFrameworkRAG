using Microsoft.Agents.AI;
using System.Threading.Tasks;

namespace MSAgentFrameworkRAG.Interfaces
{
    public interface IMetadataExtractionService
    {
        Task<AgentResponse<DocumentMetadataResult>> ExtractMetadataAsync(string filePath, string fileName);
    }

    public class DocumentMetadataResult
    {
        public string? FileName { get; set; }
        public string DocumentType { get; set; } = "Unknown";
        public string Company { get; set; } = "Unknown";
        public string FiscalQuarter { get; set; } = "N/A";
        public int FiscalYear { get; set; } = 0;
        public string PublicationDate { get; set; } = "Unknown";
        public string Version { get; set; } = "1.0";
    }
}
