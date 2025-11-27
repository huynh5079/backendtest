using BusinessLayer.Service.Interface;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Mscc.GenerativeAI; // Thư viện Mscc
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;

namespace BusinessLayer.Service
{
    public class AiAnalysisService : IAiAnalysisService
    {
        private readonly GoogleAI _googleAi;
        // Sử dụng string tên model trực tiếp để tránh lỗi phiên bản enum
        private readonly string _modelName = "gemini-1.5-pro";
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly ILogger<AiAnalysisService> _logger;

        public AiAnalysisService(IConfiguration configuration, IHttpClientFactory httpClientFactory, ILogger<AiAnalysisService> logger)
        {
            // Đọc Key từ appsettings.json
            var apiKey = configuration["GoogleAiStudio:ApiKey"];

            if (string.IsNullOrEmpty(apiKey))
            {
                throw new ArgumentNullException("GoogleAiStudio:ApiKey", "Không tìm thấy API Key trong appsettings.json. Vui lòng kiểm tra lại cấu hình.");
            }

            _googleAi = new GoogleAI(apiKey);
            _httpClientFactory = httpClientFactory;
            _logger = logger;
        }

        // 1. Phân tích file (Hỗ trợ PDF, Ảnh, Video...)
        // ... (giữ nguyên các phần khác)

        public async Task<string> AnalyzeFileAsync(string textPrompt, string fileUrl, string mimeType)
        {
            _logger.LogInformation("Đang tải file từ URL: {FileUrl} (Type: {MimeType})", fileUrl, mimeType);

            // 0. DANH SÁCH CÁC ĐỊNH DẠNG GEMINI HỖ TRỢ
            var supportedMimeTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "application/pdf",
                "image/png", "image/jpeg", "image/webp", "image/heic", "image/heif",
                "audio/wav", "audio/mp3", "audio/aiff", "audio/aac", "audio/ogg", "audio/flac",
                "video/mp4", "video/mpeg", "video/mov", "video/avi", "video/x-flv", "video/mpg", "video/webm", "video/wmv", "video/3gpp",
                "text/plain", "text/html", "text/css", "text/javascript", "application/json", "text/xml", "text/csv"
            };

            // Kiểm tra nếu file không được hỗ trợ
            if (!supportedMimeTypes.Contains(mimeType))
            {
                _logger.LogWarning("Định dạng file {MimeType} không được Gemini hỗ trợ trực tiếp.", mimeType);
                // Trả về thông báo để ChatbotService biết và bỏ qua file này, không làm lỗi cả quy trình
                return $"[HỆ THỐNG]: File này có định dạng ({mimeType}) mà AI chưa hỗ trợ đọc trực tiếp. Vui lòng upload PDF hoặc Ảnh.";
            }

            byte[] fileBytes;
            try
            {
                var client = _httpClientFactory.CreateClient();
                fileBytes = await client.GetByteArrayAsync(fileUrl);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Lỗi tải file từ URL");
                return "Lỗi: Không thể tải file tài liệu để phân tích.";
            }

            try
            {
                var model = _googleAi.GenerativeModel(model: _modelName);

                var request = new GenerateContentRequest
                {
                    Contents = new List<Content>
            {
                new Content
                {
                    Parts = new List<IPart>
                    {
                        new TextData { Text = textPrompt },
                        new InlineData { MimeType = mimeType, Data = Convert.ToBase64String(fileBytes) }
                    }
                }
            }
                };

                var response = await model.GenerateContent(request);
                return response.Text;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Lỗi khi gọi Gemini API");
                // Trả về message lỗi gọn gàng hơn để hiển thị trong chat
                return $"Lỗi phân tích AI: {ex.Message}";
            }
        }
        // 2. Chat / Chỉ văn bản
        public async Task<string> GenerateTextOnlyAsync(string textPrompt)
        {
            try
            {
                var model = _googleAi.GenerativeModel(model: _modelName);
                var response = await model.GenerateContent(textPrompt);
                return response.Text;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Lỗi khi gọi Gemini API (Text only)");
                return "AI đang bận, vui lòng thử lại sau.";
            }
        }

        // 3. Tạo Embedding (Vector) - Để dành cho tính năng Matching sau này
        public async Task<float[]> GetEmbeddingAsync(string text)
        {
            try
            {
                var model = _googleAi.GenerativeModel(model: "text-embedding-004");
                var response = await model.EmbedContent(text);

                if (response.Embedding != null && response.Embedding.Values != null)
                {
                    return response.Embedding.Values.ToArray();
                }
                return Array.Empty<float>();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Lỗi khi tạo Embedding");
                return Array.Empty<float>();
            }
        }
    }
}