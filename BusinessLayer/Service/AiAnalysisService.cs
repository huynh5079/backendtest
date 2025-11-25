//using BusinessLayer.Service.Interface;
//using Google.Ai.Generativelanguage.V1Beta; // <-- Thư viện mới
//using Microsoft.Extensions.Configuration; // Cần để đọc appsettings.json
//using Microsoft.Extensions.Logging;
//using System;
//using System.Collections.Generic;
//using System.Linq;
//using System.Net.Http; // Cần để tải file từ Cloudinary
//using System.Reflection.Metadata;
//using System.Threading.Tasks;

//namespace BusinessLayer.Service
//{
//    public class AiAnalysisService : IAiAnalysisService
//    {
//        private readonly string _apiKey;
//        private readonly GenerativeServiceClient _client;
//        private readonly IHttpClientFactory _httpClientFactory;
//        private readonly ILogger<AiAnalysisService> _logger;
//        private readonly string _model = "models/gemini-1.5-pro-preview-0409"; // Tên model của AI Studio

//        public AiAnalysisService(IConfiguration configuration, IHttpClientFactory httpClientFactory, ILogger<AiAnalysisService> logger)
//        {
//            _apiKey = configuration["GoogleAiStudio:ApiKey"]
//                      ?? throw new ArgumentNullException("GoogleAiStudio:ApiKey not found in appsettings.json");

//            // Tạo client mới
//            _client = new GenerativeServiceClientBuilder
//            {
//                ApiKey = _apiKey
//            }.Build();

//            _httpClientFactory = httpClientFactory;
//            _logger = logger;
//        }

//        // === HÀM 1: PHÂN TÍCH FILE (ĐÃ NÂNG CẤP) ===
//        public async Task<string> AnalyzeFileAsync(string textPrompt, string fileUrl, string mimeType)
//        {
//            _logger.LogInformation("Bắt đầu tải file từ URL: {FileUrl}", fileUrl);

//            // BƯỚC 1: Tải file từ Cloudinary (hoặc URL bất kỳ) về server
//            byte[] fileBytes;
//            try
//            {
//                var http = _httpClientFactory.CreateClient();
//                fileBytes = await http.GetByteArrayAsync(fileUrl);
//            }
//            catch (Exception ex)
//            {
//                _logger.LogError(ex, "Không thể tải file từ {FileUrl}", fileUrl);
//                return $"Lỗi: Không thể tải file từ URL để phân tích.";
//            }

//            _logger.LogInformation("Tải file thành công, kích thước: {Size} bytes. Gửi cho AI...", fileBytes.Length);

//            // BƯỚC 2: Xây dựng prompt (gửi nội dung file, không phải URL)
//            var content = new Content
//            {
//                Parts =
//                {
//                    new Part { Text = textPrompt },
//                    new Part
//                    {
//                        InlineData = new Blob
//                        {
//                            MimeType = mimeType,
//                            Data = ByteString.CopyFrom(fileBytes) // Gửi nội dung file
//                        }
//                    }
//                }
//            };

//            var request = new GenerateContentRequest
//            {
//                Model = _model,
//                Contents = { content }
//            };

//            // BƯỚC 3: GỌI API
//            GenerateContentResponse response = await _client.GenerateContentAsync(request);

//            // BƯỚC 4: Đọc kết quả
//            return ParseResponse(response);
//        }

//        // === HÀM 2: CHỈ VĂN BẢN (CHO CHATBOT) ===
//        public async Task<string> GenerateTextOnlyAsync(string textPrompt)
//        {
//            var content = new Content { Parts = { new Part { Text = textPrompt } } };

//            var request = new GenerateContentRequest
//            {
//                Model = _model,
//                Contents = { content }
//            };

//            GenerateContentResponse response = await _client.GenerateContentAsync(request);
//            return ParseResponse(response);
//        }

//        // Helper (Tách ra để tái sử dụng)
//        private string ParseResponse(GenerateContentResponse response)
//        {
//            try
//            {
//                // Cấu trúc response của AI Studio đơn giản hơn
//                return response.Candidates.First().Content.Parts.First().Text;
//            }
//            catch (Exception ex)
//            {
//                _logger.LogError(ex, "Lỗi khi đọc phản hồi từ AI Studio. Response: {Response}", response.ToString());
//                // Trả về thông báo (nếu AI từ chối, v.v.)
//                return $"Lỗi khi đọc phản hồi từ AI: {response.PromptFeedback?.BlockReason.ToString() ?? ex.Message}";
//            }
//        }
//    }

//    // XÓA BỎ class 'PartExtensions' (không cần thiết cho thư viện này)
//}