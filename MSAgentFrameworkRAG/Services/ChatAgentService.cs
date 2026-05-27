using System;
using System.Text;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Agents.AI;
using Microsoft.Extensions.Options;
using Pinecone;
using MSAgentFrameworkRAG.Interfaces;
using OpenAI.Chat;
using Microsoft.Extensions.AI;
using ChatMessage = Microsoft.Extensions.AI.ChatMessage;

namespace MSAgentFrameworkRAG.Services
{
    public class ChatAgentService : IChatAgentService
    {
        private readonly IConversationService _conversationService;
        private readonly IRetrievalService _retrievalService;
        private readonly IRerankService _rerankService;
        private readonly OpenAISettings _openAiSettings;
        private readonly PineconeSettings _pineconeSettings;
        private readonly AppDbContext _dbContext;

        public ChatAgentService(
            IConversationService conversationService,
            IRetrievalService retrievalService,
            IRerankService rerankService,
            IOptions<OpenAISettings> openAiOptions,
            IOptions<PineconeSettings> pineconeOptions,
            AppDbContext dbContext)
        {
            _conversationService = conversationService ?? throw new ArgumentNullException(nameof(conversationService));
            _retrievalService = retrievalService ?? throw new ArgumentNullException(nameof(retrievalService));
            _rerankService = rerankService ?? throw new ArgumentNullException(nameof(rerankService));
            _openAiSettings = openAiOptions?.Value ?? throw new ArgumentNullException(nameof(openAiOptions));
            _pineconeSettings = pineconeOptions?.Value ?? throw new ArgumentNullException(nameof(pineconeOptions));
            _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
        }

        public async Task<ChatResponse> ProcessChatAsync(ChatRequest request)
        {
            if (string.IsNullOrWhiteSpace(request.ConversationId))
            {
                throw new ArgumentException("ConversationId is required.");
            }
            if (string.IsNullOrWhiteSpace(request.Message))
            {
                throw new ArgumentException("Message is required.");
            }

            Console.WriteLine($"[Chat Agent] Processing chat for Conversation: '{request.ConversationId}', Message: '{request.Message}'");

            // 1. Fetch Conversation History from SQL Database
            var conversation = _conversationService.Get(request.ConversationId);
            if (conversation == null)
            {
                conversation = _conversationService.Create(null); // Fallback creation
            }

            // 2. Query Rewriter Agent (OpenAI Direct Chat Completion)
            // Rewrites multi-turn queries to be search-optimized standalone terms
            string standaloneQuery = request.Message;
            if (conversation.Messages != null && conversation.Messages.Count > 0)
            {
                try
                {
                    standaloneQuery = await RewriteQueryAsync(conversation.Messages, request.Message).ConfigureAwait(false);
                    Console.WriteLine($"[Chat Agent] Query rewritten: '{request.Message}' -> '{standaloneQuery}'");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[Chat Agent Error] Query rewriting failed: {ex.Message}. Using original query.");
                }
            }

            // 3. Configure Pinecone Search Adapter for Microsoft AIAgent Provider
            var pinecone = new PineconeClient(_pineconeSettings.ApiKey);
            
            // Build Pinecone Metadata filter
            Metadata? filter = null;
            if (request.DocumentIds != null && request.DocumentIds.Count > 0)
            {
                if (request.DocumentIds.Count == 1)
                {
                    filter = new Metadata { ["documentId"] = new MetadataValue(request.DocumentIds[0]) };
                }
                else
                {
                    var innerFilter = new Metadata();
                    innerFilter["$in"] = new MetadataValue(request.DocumentIds.Select(id => new MetadataValue(id)).ToArray());
                    filter = new Metadata { ["documentId"] = new MetadataValue(innerFilter) };
                    filter["isLatest"] = new MetadataValue("true");
                }
            }
            else
            {
                filter = new Metadata { ["isLatest"] = new MetadataValue("true") };
            }

            var searchAdapter = new PineconeTextSearchAdapter(
                pinecone,
                _pineconeSettings.IndexName,
                _openAiSettings.ApiKey,
                _rerankService,
                _dbContext,
                embeddingOptions: new OpenAI.Embeddings.EmbeddingGenerationOptions { Dimensions = 512 },
                filter: filter,
                embeddingModel: _openAiSettings.EmbeddingModel ?? "text-embedding-3-small",
                topK: _pineconeSettings.QueryTopK > 0 ? _pineconeSettings.QueryTopK : 40
            );

            TextSearchProviderOptions textSearchOptions = new()
            {
                SearchTime = TextSearchProviderOptions.TextSearchBehavior.BeforeAIInvoke,
            };

            // 4. Configure Microsoft AI Agent
            ChatClient client = new(model: _openAiSettings.ChatModel ?? "gpt-4o-mini", apiKey: _openAiSettings.ApiKey);

            ChatClientAgentOptions chatClientAgentOptions = new()
            {
                ChatOptions = new()
                {
                    Instructions = ContractPrompts.RagInstructions
                },
                Name = "RAGSupportAgent",
                AIContextProviders = [new TextSearchProvider(searchAdapter.SearchAsync, textSearchOptions)]
            };

            AIAgent agent = client.AsAIAgent(chatClientAgentOptions);

            // 5. Get or Create Session
            var session = await _conversationService.GetOrCreateSessionAsync(request.ConversationId, agent).ConfigureAwait(false);

            // Auto-rename chat session on the first user query
            if (conversation.Messages == null || conversation.Messages.Count == 0)
            {
                try
                {
                    string shortTitle = await GenerateChatTitleAsync(request.Message).ConfigureAwait(false);
                    _conversationService.Rename(request.ConversationId, shortTitle);
                    Console.WriteLine($"[Chat Session Auto-Rename] Renamed session '{request.ConversationId}' to '{shortTitle}'");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[Chat Session Auto-Rename ERROR] Failed to generate title: {ex.Message}");
                }
            }

            // 6. Run AIAgent
            var agentResponse = await agent.RunAsync(standaloneQuery, session).ConfigureAwait(false);
            var responseText = agentResponse.Text;

            // 7. Retrieve the exact high-precision citations already used by the LLM
            var citations = searchAdapter.LastSearchResults;

            // 8. Persist Messages in SQL Database
            var userMsg = new ChatMessageInfo
            {
                Sender = "user",
                Text = request.Message,
                Timestamp = DateTime.UtcNow
            };

            var assistantMsg = new ChatMessageInfo
            {
                Sender = "assistant",
                Text = responseText,
                Timestamp = DateTime.UtcNow,
                Citations = citations
            };

            _conversationService.AddMessage(request.ConversationId, userMsg);
            _conversationService.AddMessage(request.ConversationId, assistantMsg);

            return new ChatResponse
            {
                ConversationId = request.ConversationId,
                Message = assistantMsg
            };
        }

        private async Task<string> RewriteQueryAsync(List<ChatMessageInfo> history, string latestMessage)
        {
            var client = new OpenAI.Chat.ChatClient(model: _openAiSettings.ChatModel ?? "gpt-4o-mini", apiKey: _openAiSettings.ApiKey);

            AIAgent queryReWriteAgent = client.AsAIAgent(new ChatClientAgentOptions
            {
                Name = "QueryRewriter",
                ChatOptions = new()
                {
                    Instructions = ContractPrompts.QueryRewriteInstructions
                }
            });
            var chatMessages = new List<Microsoft.Extensions.AI.ChatMessage>();

            //Take last 5 history messages to keep context concise
            foreach (var h in history.TakeLast(5))
            {
                if (h.Sender.Equals(ChatRole.User))
                {
                    chatMessages.Add(new ChatMessage(ChatRole.User, h.Text));
                }
                else
                {
                    chatMessages.Add(new ChatMessage(ChatRole.Assistant, h.Text));
                }
            }

            chatMessages.Add(new ChatMessage(ChatRole.User, latestMessage));
            AgentResponse queryRewriteResponse = await queryReWriteAgent.RunAsync(chatMessages).ConfigureAwait(false);

            return queryRewriteResponse.Text;
        }

        public async IAsyncEnumerable<string> ProcessChatStreamAsync(ChatRequest request)
        {
            if (string.IsNullOrWhiteSpace(request.ConversationId))
            {
                throw new ArgumentException("ConversationId is required.");
            }
            if (string.IsNullOrWhiteSpace(request.Message))
            {
                throw new ArgumentException("Message is required.");
            }

            Console.WriteLine($"[Chat Agent] Processing streaming chat for Conversation: '{request.ConversationId}', Message: '{request.Message}'");

            // 1. Fetch Conversation History from SQL Database
            var conversation = _conversationService.Get(request.ConversationId);
            if (conversation == null)
            {
                conversation = _conversationService.Create(null); // Fallback creation
            }

            // 2. Query Rewriter Agent
            string standaloneQuery = request.Message;
            if (conversation.Messages != null && conversation.Messages.Count > 0)
            {
                try
                {
                    standaloneQuery = await RewriteQueryAsync(conversation.Messages, request.Message).ConfigureAwait(false);
                    Console.WriteLine($"[Chat Agent] Query rewritten: '{request.Message}' -> '{standaloneQuery}'");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[Chat Agent Error] Query rewriting failed: {ex.Message}. Using original query.");
                }
            }

            // 3. Configure Pinecone Search Adapter
            var pinecone = new PineconeClient(_pineconeSettings.ApiKey);
            
            Metadata? filter = null;
            if (request.DocumentIds != null && request.DocumentIds.Count > 0)
            {
                if (request.DocumentIds.Count == 1)
                {
                    filter = new Metadata { ["documentId"] = new MetadataValue(request.DocumentIds[0]) };
                }
                else
                {
                    var innerFilter = new Metadata();
                    innerFilter["$in"] = new MetadataValue(request.DocumentIds.Select(id => new MetadataValue(id)).ToArray());
                    filter = new Metadata { ["documentId"] = new MetadataValue(innerFilter) };
                    filter["isLatest"] = new MetadataValue("true");
                }
            }
            else
            {
                filter = new Metadata { ["isLatest"] = new MetadataValue("true") };
            }

            var searchAdapter = new PineconeTextSearchAdapter(
                pinecone,
                _pineconeSettings.IndexName,
                _openAiSettings.ApiKey,
                _rerankService,
                _dbContext,
                embeddingOptions: new OpenAI.Embeddings.EmbeddingGenerationOptions { Dimensions = 512 },
                filter: filter,
                embeddingModel: _openAiSettings.EmbeddingModel ?? "text-embedding-3-small",
                topK: _pineconeSettings.QueryTopK > 0 ? _pineconeSettings.QueryTopK : 40
            );

            TextSearchProviderOptions textSearchOptions = new()
            {
                SearchTime = TextSearchProviderOptions.TextSearchBehavior.BeforeAIInvoke,
            };

            // 4. Configure Microsoft AI Agent
            ChatClient client = new(model: _openAiSettings.ChatModel ?? "gpt-4o-mini", apiKey: _openAiSettings.ApiKey);

            ChatClientAgentOptions chatClientAgentOptions = new()
            {
                ChatOptions = new()
                {
                    Instructions = ContractPrompts.RagInstructions
                },
                Name = "RAGSupportAgent",
                AIContextProviders = [new TextSearchProvider(searchAdapter.SearchAsync, textSearchOptions)]
            };

            AIAgent agent = client.AsAIAgent(chatClientAgentOptions);

            // 5. Get or Create Session
            var session = await _conversationService.GetOrCreateSessionAsync(request.ConversationId, agent).ConfigureAwait(false);

            // 6. Run AIAgent in streaming mode
            var fullResponseText = new StringBuilder();

            await foreach (var chunk in agent.RunStreamingAsync(standaloneQuery, session).ConfigureAwait(false))
            {
                if (chunk != null && !string.IsNullOrEmpty(chunk.Text))
                {
                    fullResponseText.Append(chunk.Text);
                    yield return chunk.Text;
                }
            }

            // 7. Retrieve the exact high-precision citations already used by the LLM
            var citations = searchAdapter.LastSearchResults;

            // 8. Persist Messages in SQL Database
            var userMsg = new ChatMessageInfo
            {
                Sender = "user",
                Text = request.Message,
                Timestamp = DateTime.UtcNow
            };

            var assistantMsg = new ChatMessageInfo
            {
                Sender = "assistant",
                Text = fullResponseText.ToString(),
                Timestamp = DateTime.UtcNow,
                Citations = citations
            };

            _conversationService.AddMessage(request.ConversationId, userMsg);
            _conversationService.AddMessage(request.ConversationId, assistantMsg);

            // Auto-rename chat session on the first user query (Moved AFTER streaming for instant response start!)
            if (conversation.Messages == null || conversation.Messages.Count == 0)
            {
                try
                {
                    string shortTitle = await GenerateChatTitleAsync(request.Message).ConfigureAwait(false);
                    _conversationService.Rename(request.ConversationId, shortTitle);
                    Console.WriteLine($"[Chat Session Auto-Rename] Renamed session '{request.ConversationId}' to '{shortTitle}'");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[Chat Session Auto-Rename ERROR] Failed to generate title: {ex.Message}");
                }
            }
        }

        private async Task<string> GenerateChatTitleAsync(string firstMessage)
        {
            var client = new ChatClient(model: _openAiSettings.ChatModel ?? "gpt-4o-mini", apiKey: _openAiSettings.ApiKey);

            var options = new ChatClientAgentOptions
            {
                Name = "Session Title Agent",
                ChatOptions = new()
                {
                    Instructions = ContractPrompts.TitleInstructions
                }
            };

            AIAgent agent = client.AsAIAgent(options);
            var response = await agent.RunAsync(firstMessage).ConfigureAwait(false);
            return response.Text.Trim();
        }
    }
}