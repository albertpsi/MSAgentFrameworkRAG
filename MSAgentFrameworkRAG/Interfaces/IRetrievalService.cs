using System.Collections.Generic;
using System.Threading.Tasks;

namespace MSAgentFrameworkRAG.Interfaces
{
    public interface IRetrievalService
    {
        Task<List<SourceCitation>> RetrieveContextAsync(string query, string? documentId = null);
        Task<List<SourceCitation>> RetrieveContextAsync(string query, List<string>? documentIds);
    }
}
