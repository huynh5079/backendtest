using System.Threading.Tasks;

namespace BusinessLayer.Service.Interface
{
    public interface IChatbotService
    {
        /// <summary>
        /// Trả lời câu hỏi của học sinh dựa trên tài liệu của lớp học (RAG).
        /// </summary>
        /// <param name="actorUserId">UserId của học sinh/phụ huynh đang hỏi.</param>
        /// <param name="classId">ID của lớp học.</param>
        /// <param name="question">Câu hỏi của học sinh.</param>
        /// <returns>Câu trả lời do AI tạo ra.</returns>
        Task<string> AskClassChatbotAsync(string actorUserId, string classId, string question);
    }
}