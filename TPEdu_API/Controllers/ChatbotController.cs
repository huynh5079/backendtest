using BusinessLayer.DTOs.API;
using BusinessLayer.DTOs.Chatbot;
using BusinessLayer.Service.Interface;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using TPEdu_API.Common.Extensions;
using System.Threading.Tasks;

namespace TPEdu_API.Controllers
{
    [ApiController]
    [Route("tpedu/v1/chat")]
    [Authorize(Roles = "Student,Parent")] // Chỉ Học sinh và Phụ huynh
    public class ChatbotController : ControllerBase
    {
        private readonly IChatbotService _chatbotService;

        public ChatbotController(IChatbotService chatbotService)
        {
            _chatbotService = chatbotService;
        }

        /// <summary>
        /// [Student/Parent] Hỏi chatbot về nội dung lớp học (RAG)
        /// </summary>
        [HttpPost("{classId}/ask")]
        public async Task<IActionResult> Ask(string classId, [FromBody] ChatbotRequestDto request)
        {
            var userId = User.RequireUserId();

            // Gọi service RAG
            var answer = await _chatbotService.AskClassChatbotAsync(userId, classId, request.Question);

            var response = new ChatbotResponseDto { Answer = answer };
            return Ok(ApiResponse<ChatbotResponseDto>.Ok(response));
        }
    }
}