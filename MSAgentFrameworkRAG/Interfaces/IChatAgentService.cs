using System.Collections.Generic;
using System.Threading.Tasks;

namespace MSAgentFrameworkRAG.Interfaces
{
    public interface IChatAgentService
    {
        Task<ChatResponse> ProcessChatAsync(ChatRequest request);
        IAsyncEnumerable<string> ProcessChatStreamAsync(ChatRequest request);
    }
}
