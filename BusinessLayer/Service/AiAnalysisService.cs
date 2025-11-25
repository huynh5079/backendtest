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
        private readonly string _modelName = "gemini-2.5-flash";
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
        public async Task<string> AnalyzeFileAsync(string textPrompt, string fileUrl, string mimeType)
        {
            _logger.LogInformation("Đang tải file từ URL: {FileUrl}", fileUrl);

            byte[] fileBytes;
            try
            {
                // Tải file về RAM
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
                // Khởi tạo model
                var model = _googleAi.GenerativeModel(model: _modelName);

                // Tạo nội dung gửi đi (bao gồm Text và File Base64)
                // Mscc hỗ trợ rất đơn giản, không cần Protobuf phức tạp
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
                return $"Lỗi hệ thống AI: {ex.Message}";
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