using System;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using MSAgentFrameworkRAG.Interfaces;

namespace MSAgentFrameworkRAG.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class ChatController : ControllerBase
    {
        private readonly IChatAgentService _chatAgentService;

        public ChatController(IChatAgentService chatAgentService)
        {
            _chatAgentService = chatAgentService ?? throw new ArgumentNullException(nameof(chatAgentService));
        }

        // POST /api/chat
        [HttpPost]
        public async Task<IActionResult> ProcessChat([FromBody] ChatRequest request)
        {
            if (request == null)
            {
                return BadRequest("Invalid chat request.");
            }

            try
            {
                var response = await _chatAgentService.ProcessChatAsync(request);
                return Ok(response);
            }
            catch (ArgumentException argEx)
            {
                return BadRequest(argEx.Message);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ChatController ERROR] Exception: {ex}");
                return StatusCode(500, new { error = ex.Message, details = ex.ToString() });
            }
        }

        // POST /api/chat/stream
        [HttpPost("stream")]
        public async Task ProcessChatStream([FromBody] ChatRequest request)
        {
            if (request == null)
            {
                Response.StatusCode = 400;
                await Response.WriteAsync("Invalid chat request.");
                return;
            }

            Response.Headers.Append("Content-Type", "text/event-stream");
            Response.Headers.Append("Cache-Control", "no-cache, no-transform");
            Response.Headers.Append("Connection", "keep-alive");
            Response.Headers.Append("X-Accel-Buffering", "no");

            var responseFeature = HttpContext.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpResponseBodyFeature>();
            if (responseFeature != null)
            {
                responseFeature.DisableBuffering();
            }

            try
            {
                await foreach (var chunk in _chatAgentService.ProcessChatStreamAsync(request))
                {
                    if (HttpContext.RequestAborted.IsCancellationRequested)
                    {
                        break;
                    }
                    var formattedChunk = System.Text.Json.JsonSerializer.Serialize(new { text = chunk });
                    await Response.WriteAsync($"data: {formattedChunk}\n\n");
                    await Response.Body.FlushAsync();
                }

                await Response.WriteAsync("event: done\ndata: [DONE]\n\n");
                await Response.Body.FlushAsync();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ChatController stream ERROR] Exception: {ex}");
                var errorPayload = System.Text.Json.JsonSerializer.Serialize(new { error = ex.Message });
                await Response.WriteAsync($"event: error\ndata: {errorPayload}\n\n");
                await Response.Body.FlushAsync();
            }
        }
    }
}
