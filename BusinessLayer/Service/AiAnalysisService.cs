// Thêm các using này ở đầu file
using Google.Cloud.AIPlatform.V1;
// Bỏ using Google.Protobuf.WellKnownTypes; để tránh lỗi mơ hồ
using BusinessLayer.Service.Interface;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Linq; // Cần cho .Select()

namespace BusinessLayer.Service
{
    public class AiAnalysisService : IAiAnalysisService
    {
        private readonly PredictionServiceClient _client;
        private readonly string _projectId = "tpedu-ai-project"; // <-- THAY BẰNG PROJECT ID CỦA BẠN
        private readonly string _location = "us-central1";     // <-- THAY BẰNG REGION CỦA BẠN (vd: asia-southeast1)
        private readonly string _model = "gemini-1.5-pro-preview-0409"; // Tên mô hình

        public AiAnalysisService()
        {
            string endpoint = $"{_location}-aiplatform.googleapis.com";
            var clientBuilder = new PredictionServiceClientBuilder
            {
                Endpoint = endpoint
            };
            _client = clientBuilder.Build();
        }

        public async Task<string> AnalyzeFileRelevanceAsync(string contextTitle, string fileUrl, string mimeType)
        {
            // 3. Xây dựng prompt đa phương thức (multimodal)
            var promptParts = new List<Part>
            {
                new Part { Text = $"Bạn là trợ lý kiểm duyệt. Hãy phân tích file sau đây." },
                new Part { Text = $"Chủ đề yêu cầu là: '{contextTitle}'." },
                new Part { Text = "Nội dung file có liên quan đến chủ đề này không? File có chứa nội dung nhạy cảm (var) không? Trả lời ngắn gọn." },
                new Part
                {
                    FileData = new FileData
                    {
                        MimeType = mimeType,
                        FileUri = fileUrl
                    }
                }
            };

            // 4. === SỬA LỖI: Xây dựng yêu cầu (Request) từ trong ra ngoài ===

            // 4.1. Tạo ListValue cho 'parts'
            // Dùng tên đầy đủ Google.Protobuf.WellKnownTypes.ListValue
            var partsListValue = new Google.Protobuf.WellKnownTypes.ListValue();

            // Dùng .AddRange() thay vì gán (Đây là hàm helper PartExtensions.ToValue())
            partsListValue.Values.AddRange(promptParts.Select(p => p.ToValue()));

            // 4.2. Tạo 'content' struct (ĐÂY LÀ DÒNG SỬA LỖI CS1503)
            var contentStruct = new Google.Protobuf.WellKnownTypes.Struct();
            contentStruct.Fields.Add("parts", Google.Protobuf.WellKnownTypes.Value.ForList(partsListValue)); // <--- Phải bọc trong .ForList()

            // 4.3. Tạo 'instance' struct
            var instanceStruct = new Google.Protobuf.WellKnownTypes.Struct();
            instanceStruct.Fields.Add("content", Google.Protobuf.WellKnownTypes.Value.ForStruct(contentStruct)); // <--- Phải bọc trong .ForStruct()

            // 4.4. Tạo 'instance' Value
            var instanceValue = Google.Protobuf.WellKnownTypes.Value.ForStruct(instanceStruct);

            // 4.5. Tạo request
            var request = new PredictRequest
            {
                Endpoint = EndpointName.FromProjectLocationPublisherModel(
                    _projectId, _location, "google", _model
                ).ToString(),
            };

            // Dùng .Add() vì Instances là read-only
            request.Instances.Add(instanceValue);

            // === KẾT THÚC SỬA LỖI ===

            // 5. GỌI API
            PredictResponse response = await _client.PredictAsync(request);

            // 6. Đọc kết quả (Response)
            try
            {
                var prediction = response.Predictions.First();
                // Dùng tên đầy đủ để tránh mơ hồ
                var candidate = prediction.StructValue.Fields["candidates"].ListValue.Values[0];
                var content = candidate.StructValue.Fields["content"].StructValue;
                var textResponse = content.Fields["parts"].ListValue.Values[0].StructValue.Fields["text"].StringValue;

                return textResponse;
            }
            catch (System.Exception ex)
            {
                return $"Lỗi khi đọc phản hồi từ AI: {ex.Message}";
            }
        }
    }

    // Helper class để chuyển Part -> Struct (Bắt buộc cho API)
    public static class PartExtensions
    {
        // Đổi tên ToStruct thành ToValue để logic rõ ràng hơn
        public static Google.Protobuf.WellKnownTypes.Value ToValue(this Part part)
        {
            // Dùng tên đầy đủ Google.Protobuf.WellKnownTypes.Struct
            var s = new Google.Protobuf.WellKnownTypes.Struct();
            if (part.Text != null)
                s.Fields.Add("text", Google.Protobuf.WellKnownTypes.Value.ForString(part.Text));

            if (part.FileData != null)
                s.Fields.Add("file_data", Google.Protobuf.WellKnownTypes.Value.ForStruct(new Google.Protobuf.WellKnownTypes.Struct
                {
                    Fields =
                    {
                        { "mime_type", Google.Protobuf.WellKnownTypes.Value.ForString(part.FileData.MimeType) },
                        { "file_uri", Google.Protobuf.WellKnownTypes.Value.ForString(part.FileData.FileUri) }
                    }
                }));

            return Google.Protobuf.WellKnownTypes.Value.ForStruct(s);
        }
    }
}