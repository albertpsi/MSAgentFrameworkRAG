using System.Collections.Concurrent;
using Microsoft.Agents.AI;

namespace MSAgentFrameworkRAG
{
    public class SessionCache
    {
        private readonly ConcurrentDictionary<string, AgentSession> _sessions = new();

        public AgentSession? Get(string conversationId)
        {
            return _sessions.TryGetValue(conversationId, out var session) ? session : null;
        }

        public void Set(string conversationId, AgentSession session)
        {
            _sessions[conversationId] = session;
        }
    }
}
