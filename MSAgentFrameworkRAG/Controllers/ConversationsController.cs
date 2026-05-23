using System;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using MSAgentFrameworkRAG.Interfaces;

namespace MSAgentFrameworkRAG.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class ConversationsController : ControllerBase
    {
        private readonly IConversationService _conversationService;

        public ConversationsController(IConversationService conversationService)
        {
            _conversationService = conversationService ?? throw new ArgumentNullException(nameof(conversationService));
        }

        // GET /api/conversations
        [HttpGet]
        public IActionResult GetConversations()
        {
            return Ok(_conversationService.GetAll());
        }

        // POST /api/conversations/new
        [HttpPost("new")]
        public IActionResult CreateConversation([FromBody] JsonElement body)
        {
            string? name = null;
            if (body.ValueKind == JsonValueKind.Object && body.TryGetProperty("name", out var prop))
            {
                name = prop.GetString();
            }
            var conversation = _conversationService.Create(name);
            return Ok(conversation);
        }

        // GET /api/conversations/{id}
        [HttpGet("{id}")]
        public IActionResult GetConversationDetails(string id)
        {
            var convo = _conversationService.Get(id);
            if (convo == null)
            {
                return NotFound();
            }
            return Ok(convo);
        }

        // PUT /api/conversations/{id}
        [HttpPut("{id}")]
        public IActionResult RenameConversation(string id, [FromBody] JsonElement body)
        {
            string? name = null;
            if (body.ValueKind == JsonValueKind.Object && body.TryGetProperty("name", out var prop))
            {
                name = prop.GetString();
            }

            if (string.IsNullOrWhiteSpace(name))
            {
                return BadRequest("Name is required.");
            }

            var success = _conversationService.Rename(id, name);
            if (!success)
            {
                return NotFound();
            }

            return Ok(new { message = "Renamed successfully" });
        }

        // DELETE /api/conversations/{id}
        [HttpDelete("{id}")]
        public IActionResult DeleteConversation(string id)
        {
            var success = _conversationService.Delete(id);
            if (!success)
            {
                return NotFound();
            }

            return Ok(new { message = "Deleted successfully" });
        }
    }
}