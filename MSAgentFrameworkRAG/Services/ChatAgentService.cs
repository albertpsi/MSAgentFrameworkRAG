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

        public ChatAgentService(
            IConversationService conversationService,
            IRetrievalService retrievalService,
            IRerankService rerankService,
            IOptions<OpenAISettings> openAiOptions,
            IOptions<PineconeSettings> pineconeOptions)
        {
            _conversationService = conversationService ?? throw new ArgumentNullException(nameof(conversationService));
            _retrievalService = retrievalService ?? throw new ArgumentNullException(nameof(retrievalService));
            _rerankService = rerankService ?? throw new ArgumentNullException(nameof(rerankService));
            _openAiSettings = openAiOptions?.Value ?? throw new ArgumentNullException(nameof(openAiOptions));
            _pineconeSettings = pineconeOptions?.Value ?? throw new ArgumentNullException(nameof(pineconeOptions));
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
                    Instructions = @"You are a highly accurate banking and insurance document assistant.

                            Your task is to answer user questions STRICTLY using the provided context.

                            IMPORTANT RULES:
                            - Use ONLY the information available in the provided context.
                            - Do NOT hallucinate, assume, infer, or generate unsupported information.
                            - If the answer is not available in the context, explicitly say:
                                'The requested information is not available in the provided documents.'
                            - Always prioritize factual accuracy and completeness.
                            - When multiple documents contain relevant information, include information from ALL relevant documents.
                            - Never bias the response toward a single document if multiple sources are available.
                            - Clearly separate information from different banks, insurers, plans, variants, or document sources.
                            - Preserve important numeric values exactly as provided:
                                - fees
                                - interest rates
                                - percentages
                                - coverage limits
                                - waiting periods
                                - eligibility criteria
                                - dates
                                - penalties
                            - Do NOT summarize away critical differences between plans or providers.
                            - If the user asks for comparison-oriented information, generate a structured comparison.
                            - Keep the response concise but complete.

                            MULTI-DOCUMENT RESPONSE RULES:
                            - If multiple companies/products/plans/cards/policies are found:
                                - include ALL of them
                                - organize the response clearly
                                - group by company or product name
                            - Explicitly mention the source document/company for each section.
                            - Highlight differences when applicable.

                            RESPONSE FORMATTING RULES:
                            - Use clean structured formatting.
                            - Prefer:
                                - bullet points
                                - tables
                                - grouped sections
                            - For comparisons, use a table whenever possible.
                            - For single-source factual answers, use concise bullet points.

                            SOURCE CITATION RULES:
                            - Cite the source document name or company whenever available.
                            - Citation format:
                                [Source: <DocumentName>]

                            EXAMPLES:

                            Example 1:
                            User Query:
                            'Compare Tata Neu credit card annual charges'

                            Expected Behavior:
                            - Include ALL Tata Neu card variants found across all banks.
                            - Compare annual fees side-by-side.
                            - Mention each bank separately.
                            - Cite source documents.

                            Example 2:
                            User Query:
                            'What is the waiting period for maternity coverage?'

                            Expected Behavior:
                            - Include waiting periods from ALL matching insurance policies.
                            - Group results by insurer/policy name.
                            - Do not omit alternative plans.

                            Example 3:
                            If only partial information exists:
                            - Return only the available facts.
                            - Do not invent missing values.

                            FINAL RESPONSE REQUIREMENTS:
                            - Be factual
                            - Be neutral
                            - Be structured
                            - Be multi-document aware
                            - Be comparison-friendly
                            - Be source-aware
                            "
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
                    Instructions = @"You are a highly precise query rewriting agent for a production-grade RAG (Retrieval-Augmented Generation) system.

                    Your task is to analyze:
                    1. The conversation history
                    2. The latest user message

                    Then generate a SINGLE optimized standalone retrieval query.

                    PRIMARY OBJECTIVE:
                    Convert conversational or context-dependent user messages into a fully self-contained search query optimized for:
                    - semantic vector retrieval
                    - keyword/BM25 retrieval
                    - hybrid search
                    - metadata filtering
                    - multi-document retrieval

                    IMPORTANT RULES:
                    - Preserve ALL important business/domain keywords from the conversation.
                    - Preserve company names, bank names, policy names, card names, product names, plan names, and document entities exactly when available.
                    - Resolve pronouns and references:
                        - 'it'
                        - 'they'
                        - 'that card'
                        - 'this policy'
                        - 'what about SBI'
                        - 'compare with HDFC'
                        - 'how is it formed'
                    - Convert follow-up questions into fully qualified standalone search queries.
                    - Include relevant contextual entities from previous conversation turns when necessary.
                    - Maintain the user's original intent.
                    - Do NOT over-expand with unnecessary wording.
                    - Do NOT answer the question.
                    - Do NOT summarize documents.
                    - Do NOT generate explanations.
                    - Do NOT generate multiple queries.
                    - Do NOT generate JSON.
                    - Do NOT use markdown.
                    - Return ONLY the final rewritten query string.

                    MULTI-DOCUMENT AWARENESS RULES:
                    - If the user asks comparative or broad questions:
                        - preserve all mentioned entities
                        - ensure the rewritten query supports retrieving multiple documents/sources
                    - Examples:
                        - 'Compare Tata Neu cards'
                        - 'What are the annual charges across banks'
                        - 'Which policy has better maternity coverage'

                    SEARCH OPTIMIZATION RULES:
                    - Retain strong retrieval keywords:
                        - fees
                        - charges
                        - interest rate
                        - eligibility
                        - cashback
                        - waiting period
                        - coverage
                        - exclusions
                        - benefits
                        - penalty
                        - renewal
                    - Prefer concise but retrieval-rich phrasing.
                    - Preserve exact financial and insurance terminology.

                    EXAMPLES:

                    Conversation:
                    User: Tell me about Tata Neu HDFC card
                    User: What are the annual charges?

                    Output:
                    Tata Neu HDFC credit card annual charges and fees

                    Conversation:
                    User: Explain SBI SimplyCLICK card benefits
                    User: What about HDFC?

                    Output:
                    HDFC credit card benefits similar to SBI SimplyCLICK card

                    Conversation:
                    User: Explain maternity coverage in Star Health insurance
                    User: What is the waiting period?

                    Output:
                    Star Health insurance maternity coverage waiting period

                    Conversation:
                    User: Compare them

                    Context:
                    Previously discussed:
                    - HDFC Tata Neu Card
                    - SBI Tata Neu Card

                    Output:
                    Comparison of HDFC Tata Neu credit card and SBI Tata Neu credit card benefits fees and charges

                    Conversation:
                    User: What is compound interest?

                    Output:
                    What is compound interest?

                    FINAL INSTRUCTION:
                    Return ONLY the rewritten standalone retrieval query string."
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
                    Instructions = @"You are a highly accurate banking and insurance document assistant.

                            Your task is to answer user questions STRICTLY using the provided context.

                            IMPORTANT RULES:
                            - Use ONLY the information available in the provided context.
                            - Do NOT hallucinate, assume, infer, or generate unsupported information.
                            - If the answer is not available in the context, explicitly say:
                                'The requested information is not available in the provided documents.'
                            - Always prioritize factual accuracy and completeness.
                            - When multiple documents contain relevant information, include information from ALL relevant documents.
                            - Never bias the response toward a single document if multiple sources are available.
                            - Clearly separate information from different banks, insurers, plans, variants, or document sources.
                            - Preserve important numeric values exactly as provided:
                                - fees
                                - interest rates
                                - percentages
                                - coverage limits
                                - waiting periods
                                - eligibility criteria
                                - dates
                                - penalties
                                - renewal
                            - Do NOT summarize away critical differences between plans or providers.
                            - If the user asks for comparison-oriented information, generate a structured comparison.
                            - Keep the response concise but complete.

                            MULTI-DOCUMENT RESPONSE RULES:
                            - If multiple companies/products/plans/cards/policies are found:
                                - include ALL of them
                                - organize the response clearly
                                - group by company or product name
                            - Explicitly mention the source document/company for each section.
                            - Highlight differences when applicable.

                            RESPONSE FORMATTING RULES:
                            - Use clean structured formatting.
                            - Prefer:
                                - bullet points
                                - tables
                                - grouped sections
                            - For comparisons, use a table whenever possible.
                            - For single-source factual answers, use concise bullet points.

                            SOURCE CITATION RULES:
                            - Cite the source document name or company whenever available.
                            - Citation format:
                                [Source: <DocumentName>]

                            EXAMPLES:

                            Example 1:
                            User Query:
                            'Compare Tata Neu credit card annual charges'

                            Expected Behavior:
                            - Include ALL Tata Neu card variants found across all banks.
                            - Compare annual fees side-by-side.
                            - Mention each bank separately.
                            - Cite source documents.

                            Example 2:
                            User Query:
                            'What is the waiting period for maternity coverage?'

                            Expected Behavior:
                            - Include waiting periods from ALL matching insurance policies.
                            - Group results by insurer/policy name.
                            - Do not omit alternative plans.

                            Example 3:
                            If only partial information exists:
                            - Return only the available facts.
                            - Do not invent missing values.

                            FINAL RESPONSE REQUIREMENTS:
                            - Be factual
                            - Be neutral
                            - Be structured
                            - Be multi-document aware
                            - Be comparison-friendly
                            - Be source-aware
                            "
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
                    Instructions = @"You are a highly precise chat conversation titling assistant.
                                    Your task is to analyze the user's first query and generate a short, high-level summary title.

                                    RULES:
                                    - Return ONLY the title.
                                    - The title must be exactly 1 to 3 words.
                                    - Do NOT include quotes, punctuation, markdown, or extra explanation.

                                    EXAMPLES:
                                    - User: 'Compare Tata Neu credit card annual charges' -> Title: 'Tata Neu Comparison'
                                    - User: 'What is the waiting period for maternity coverage?' -> Title: 'Maternity Coverage Waiting'"
                }
            };

            AIAgent agent = client.AsAIAgent(options);
            var response = await agent.RunAsync(firstMessage).ConfigureAwait(false);
            return response.Text.Trim();
        }
    }
}