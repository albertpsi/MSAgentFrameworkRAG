using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Agents.AI;

namespace MSAgentFrameworkRAG.Interfaces
{
    public interface IConversationService
    {
        List<Conversation> GetAll();
        Conversation? Get(string id);
        Conversation Create(string? name = null, string? id = null);
        void AddMessage(string conversationId, ChatMessageInfo msg);
        bool Delete(string id);
        bool Rename(string id, string name);
        Task<AgentSession> GetOrCreateSessionAsync(string conversationId, AIAgent agent);
    }
}
