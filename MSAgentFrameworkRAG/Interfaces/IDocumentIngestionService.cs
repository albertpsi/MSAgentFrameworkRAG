using System.Threading.Tasks;

namespace MSAgentFrameworkRAG.Interfaces
{
    public interface IDocumentIngestionService
    {
        Task IngestDocumentAsync(string documentId, string filePath, string fileName);
    }
}
