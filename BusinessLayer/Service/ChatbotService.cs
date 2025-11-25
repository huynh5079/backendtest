using BusinessLayer.Service.Interface;
using DataLayer.Entities;
using DataLayer.Enum;
using DataLayer.Repositories.Abstraction;
using Microsoft.EntityFrameworkCore; // Cần để dùng .Include()
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
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
            _logger.LogInformation("Chatbot nhận câu hỏi về ClassId: {ClassId} từ User: {UserId}", classId, actorUserId);

            // 1. Retrieve (Truy xuất tài liệu)
            // Lấy tất cả file Media thuộc về lớp học này (bao gồm cả file trong các Lesson)
            // Lưu ý: Chỉ lấy file là tài liệu học tập (Material), có thể mở rộng lấy cả Video nếu muốn
            var materials = await _uow.Media.GetAllAsync(
                filter: m => m.Lesson.ClassId == classId
                          && m.Context == UploadContext.Material
                          && m.DeletedAt == null,
                includes: q => q.Include(m => m.Lesson) // Join bảng Lesson để check ClassId
            );

            if (!materials.Any())
            {
                return "Hiện tại lớp học này chưa có tài liệu nào được tải lên, nên tôi chưa thể trả lời câu hỏi của bạn dựa trên ngữ cảnh lớp học.";
            }

            // 2. Augment (Xây dựng ngữ cảnh)
            var contextBuilder = new StringBuilder();
            contextBuilder.AppendLine("Bạn là trợ giảng AI thông minh. Nhiệm vụ của bạn là trả lời câu hỏi của học sinh DỰA TRÊN các tài liệu được cung cấp dưới đây.");
            contextBuilder.AppendLine("Nếu thông tin không có trong tài liệu, hãy nói rõ là bạn không tìm thấy thông tin.");
            contextBuilder.AppendLine("\n--- BẮT ĐẦU TÀI LIỆU LỚP HỌC ---");

            int index = 1;
            foreach (var item in materials)
            {
                // Chỉ xử lý các file có định dạng văn bản hoặc ảnh/pdf mà AI đọc được
                // Bỏ qua các file quá nặng hoặc không hỗ trợ nếu cần
                try
                {
                    // Gọi AI để "đọc" file này ngay lập tức (Real-time analysis)
                    // Prompt này yêu cầu AI trích xuất nội dung quan trọng
                    string fileContent = await _aiAnalysisService.AnalyzeFileAsync(
                        "Hãy tóm tắt chi tiết nội dung của file này để làm dữ liệu trả lời câu hỏi.",
                        item.FileUrl,
                        item.MediaType
                    );

                    contextBuilder.AppendLine($"\n[Tài liệu #{index}: {item.FileName}]");
                    contextBuilder.AppendLine(fileContent);
                    index++;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning("Không thể phân tích file {FileName}: {Message}", item.FileName, ex.Message);
                }
            }

            contextBuilder.AppendLine("--- KẾT THÚC TÀI LIỆU ---");

            // Thêm câu hỏi của học sinh vào cuối
            contextBuilder.AppendLine($"\nCâu hỏi của học sinh: \"{question}\"");
            contextBuilder.AppendLine("Câu trả lời của bạn:");

            // 3. Generate (Tạo câu trả lời cuối cùng)
            // Lúc này contextBuilder chứa toàn bộ kiến thức của lớp học + câu hỏi
            string finalAnswer = await _aiAnalysisService.GenerateTextOnlyAsync(contextBuilder.ToString());

            return finalAnswer;
        }
    }
}