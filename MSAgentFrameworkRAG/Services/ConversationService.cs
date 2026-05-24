using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Agents.AI;
using Microsoft.EntityFrameworkCore;
using MSAgentFrameworkRAG.Interfaces;

namespace MSAgentFrameworkRAG.Services
{
    public class ConversationService : IConversationService
    {
        private readonly AppDbContext _dbContext;
        private readonly SessionCache _sessionCache;

        public ConversationService(AppDbContext dbContext, SessionCache sessionCache)
        {
            _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
            _sessionCache = sessionCache ?? throw new ArgumentNullException(nameof(sessionCache));
        }

        public List<Conversation> GetAll()
        {
            return _dbContext.Conversations
                .Select(c => new Conversation
                {
                    Id = c.Id,
                    Name = c.Name,
                    CreatedAt = c.CreatedAt,
                    Messages = c.Messages.Select(m => new ChatMessageInfo
                    {
                        Sender = m.Sender,
                        Text = m.Text,
                        Timestamp = m.Timestamp,
                        Citations = string.IsNullOrEmpty(m.CitationsJson)
                            ? null
                            : JsonSerializer.Deserialize<List<SourceCitation>>(m.CitationsJson, (JsonSerializerOptions?)null)
                    }).ToList()
                })
                .OrderByDescending(c => c.CreatedAt)
                .ToList();
        }

        public Conversation? Get(string id)
        {
            var dbConvo = _dbContext.Conversations
                .Include(c => c.Messages)
                .FirstOrDefault(c => c.Id == id);

            if (dbConvo == null) return null;

            return new Conversation
            {
                Id = dbConvo.Id,
                Name = dbConvo.Name,
                CreatedAt = dbConvo.CreatedAt,
                Messages = dbConvo.Messages.OrderBy(m => m.Timestamp).Select(m => new ChatMessageInfo
                {
                    Sender = m.Sender,
                    Text = m.Text,
                    Timestamp = m.Timestamp,
                    Citations = string.IsNullOrEmpty(m.CitationsJson)
                        ? null
                        : JsonSerializer.Deserialize<List<SourceCitation>>(m.CitationsJson, (JsonSerializerOptions?)null)
                }).ToList()
            };
        }

        public Conversation Create(string? name = null, string? id = null)
        {
            var convoId = string.IsNullOrWhiteSpace(id) ? Guid.NewGuid().ToString("N") : id;
            var dbConvo = new DbConversation
            {
                Id = convoId,
                Name = string.IsNullOrWhiteSpace(name) ? $"Conversation {DateTime.Now:HH:mm:ss}" : name,
                CreatedAt = DateTime.UtcNow
            };

            _dbContext.Conversations.Add(dbConvo);
            _dbContext.SaveChanges();

            return new Conversation
            {
                Id = dbConvo.Id,
                Name = dbConvo.Name,
                CreatedAt = dbConvo.CreatedAt,
                Messages = new List<ChatMessageInfo>()
            };
        }

        public void AddMessage(string conversationId, ChatMessageInfo msg)
        {
            var dbMsg = new DbChatMessage
            {
                ConversationId = conversationId,
                Sender = msg.Sender,
                Text = msg.Text,
                Timestamp = msg.Timestamp,
                CitationsJson = msg.Citations == null ? null : JsonSerializer.Serialize(msg.Citations)
            };

            _dbContext.ChatMessages.Add(dbMsg);
            _dbContext.SaveChanges();
        }

        public bool Delete(string id)
        {
            var dbConvo = _dbContext.Conversations.Find(id);
            if (dbConvo == null) return false;

            _dbContext.Conversations.Remove(dbConvo);
            _dbContext.SaveChanges();
            return true;
        }

        public bool Rename(string id, string name)
        {
            var dbConvo = _dbContext.Conversations.Find(id);
            if (dbConvo == null) return false;

            dbConvo.Name = name;
            _dbContext.SaveChanges();
            return true;
        }

        public async Task<AgentSession> GetOrCreateSessionAsync(string conversationId, AIAgent agent)
        {
            var session = _sessionCache.Get(conversationId);
            if (session != null) return session;

            var newSession = await agent.CreateSessionAsync().ConfigureAwait(false);
            _sessionCache.Set(conversationId, newSession);

            // Ensure the conversation exists in the database
            var dbConvo = _dbContext.Conversations.Find(conversationId);
            if (dbConvo == null)
            {
                Create(name: null, id: conversationId); // Fallback creation if not exist
            }

            return newSession;
        }
    }
}