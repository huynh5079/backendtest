using BusinessLayer.Service.Interface;
using DataLayer.Entities;
using DataLayer.Enum;
using DataLayer.Repositories.Abstraction;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using System;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace BusinessLayer.Service
{
    public class ChatbotService : IChatbotService
    {
        private readonly IUnitOfWork _uow;
        private readonly IAiAnalysisService _aiAnalysisService;
        private readonly ILogger<ChatbotService> _logger;

        public ChatbotService(
            IUnitOfWork uow,
            IAiAnalysisService aiAnalysisService,
            ILogger<ChatbotService> logger)
        {
            _uow = uow;
            _aiAnalysisService = aiAnalysisService;
            _logger = logger;
        }

        public async Task<string> AskClassChatbotAsync(string actorUserId, string classId, string question)
        {
            // === 1. Xác thực quyền (Authentication) ===
            // (Bạn có thể dùng lại logic tương tự như trong LessonMaterialService.ListAsync 
            // để kiểm tra xem actorUserId (là Student/Parent) có quyền truy cập classId này không)
            // ... (Tạm bỏ qua để tập trung vào logic AI) ...
            _logger.LogInformation("Chatbot được hỏi về ClassId: {ClassId} bởi User: {ActorId}", classId, actorUserId);


            // === 2. Retrieve (Truy xuất) ===
            // Lấy TẤT CẢ các file media (tài liệu) thuộc về lớp này
            var materials = await _uow.Media.GetAllAsync(
                filter: m => m.Lesson.ClassId == classId && m.Context == UploadContext.Material && m.DeletedAt == null,
                includes: q => q.Include(m => m.Lesson)
            );

            if (!materials.Any())
            {
                return "Xin lỗi, lớp học này chưa có tài liệu nào để tôi có thể trả lời câu hỏi của bạn.";
            }

            // === 3. Augment (Bổ sung - Xây dựng bối cảnh) ===
            var contextBuilder = new StringBuilder();
            contextBuilder.AppendLine("Bạn là một trợ giảng AI. Hãy trả lời câu hỏi của học sinh chỉ dựa trên các tài liệu được cung cấp dưới đây:");
            contextBuilder.AppendLine("--- BẮT ĐẦU TÀI LIỆU ---");

            int fileCount = 0;
            foreach (var material in materials)
            {
                // Đây là lúc tái sử dụng AiAnalysisService
                // Yêu cầu 1b: Chuyển file (mp4, pdf...) về dạng văn bản AI hiểu được
                try
                {
                    string fileContext = await _aiAnalysisService.AnalyzeFileAsync(
                        "Tóm tắt nội dung chính của file này để chuẩn bị trả lời câu hỏi của học sinh.",
                        material.FileUrl,
                        material.MediaType
                    );

                    contextBuilder.AppendLine($"\n[Tài liệu {++fileCount}: {material.FileName}]");
                    contextBuilder.AppendLine(fileContext);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Lỗi khi tóm tắt file {FileUrl} cho RAG", material.FileUrl);
                    contextBuilder.AppendLine($"\n[Tài liệu {++fileCount}: {material.FileName}] - (Không thể đọc nội dung file này)");
                }
            }

            contextBuilder.AppendLine("--- KẾT THÚC TÀI LIỆU ---");
            contextBuilder.AppendLine($"\nCâu hỏi của học sinh: \"{question}\"");
            contextBuilder.AppendLine("\nHãy trả lời câu hỏi trên.");

            string finalPrompt = contextBuilder.ToString();
            _logger.LogInformation("Đã tạo prompt RAG, tổng độ dài: {Length}", finalPrompt.Length);

            // === 4. Generate (Tạo sinh) ===
            // Gọi AI một lần cuối với toàn bộ bối cảnh
            string finalAnswer = await _aiAnalysisService.GenerateTextOnlyAsync(finalPrompt);

            return finalAnswer;
        }
    }
}