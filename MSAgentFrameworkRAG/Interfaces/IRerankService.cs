using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace MSAgentFrameworkRAG.Interfaces
{
    public interface IRerankService
    {
        Task<List<SourceCitation>> RerankAsync(string query, List<SourceCitation> candidates, CancellationToken cancellationToken = default);
    }
}
