using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Threading.Tasks;

namespace BusinessLayer.Service.Interface
{
    public interface IAiAnalysisService
    {
        // Dùng cho tính năng Upload/Kiểm duyệt
        Task<string> AnalyzeFileAsync(string textPrompt, string fileUrl, string mimeType);

        // Dùng cho Chatbot
        Task<string> GenerateTextOnlyAsync(string textPrompt);

        // Dùng cho tính năng Matching (Gợi ý)
        Task<float[]> GetEmbeddingAsync(string text);
    }
}
