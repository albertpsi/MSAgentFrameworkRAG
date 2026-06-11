using System.Threading;
using System.Threading.Tasks;

namespace MSAgentFrameworkRAG.Interfaces
{
    public interface IDocumentParser
    {
        Task<ParsedDocument> ParseAsync(string documentId, string filePath, CancellationToken cancellationToken = default);
    }
}
