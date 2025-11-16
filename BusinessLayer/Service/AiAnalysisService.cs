using Google.Cloud.AIPlatform.V1;
using BusinessLayer.Service.Interface;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Linq;

namespace BusinessLayer.Service
{
    public class AiAnalysisService : IAiAnalysisService
    {
        private readonly PredictionServiceClient _client;
        private readonly string _projectId = "tpedu-ai-project";
        private readonly string _location = "asia-southeast1";
        private readonly string _model = "gemini-1.5-pro-preview-0409";

        public AiAnalysisService()
        {
            string endpoint = $"{_location}-aiplatform.googleapis.com";
            var clientBuilder = new PredictionServiceClientBuilder
            {
                Endpoint = endpoint
            };
            _client = clientBuilder.Build();
        }

        // === HÀM 1: PHÂN TÍCH FILE ===
        public async Task<string> AnalyzeFileAsync(string textPrompt, string fileUrl, string mimeType)
        {
            var promptParts = new List<Part>
            {
                new Part { Text = textPrompt },
                new Part
                {
                    FileData = new FileData
                    {
                        MimeType = mimeType,
                        FileUri = fileUrl
                    }
                }
            };

            var request = BuildRequest(promptParts);
            PredictResponse response = await _client.PredictAsync(request);
            return ParseResponse(response);
        }

        // === HÀM 2: CHỈ VĂN BẢN (CHO CHATBOT) ===
        public async Task<string> GenerateTextOnlyAsync(string textPrompt)
        {
            var promptParts = new List<Part>
            {
                new Part { Text = textPrompt }
            };

            var request = BuildRequest(promptParts);
            PredictResponse response = await _client.PredictAsync(request);
            return ParseResponse(response);
        }

        // === HELPER 1: XÂY DỰNG REQUEST (DÙNG CHUNG) ===
        private PredictRequest BuildRequest(List<Part> promptParts)
        {
            // 4.1. Tạo list các Value từ promptParts
            var partsValues = promptParts.Select(p => p.ToValue()).ToList();

            // 4.2. Tạo 'content' struct
            var contentStruct = new Google.Protobuf.WellKnownTypes.Struct();
            var partsListValue = new Google.Protobuf.WellKnownTypes.Value
            {
                ListValue = new Google.Protobuf.WellKnownTypes.ListValue()
            };
            partsListValue.ListValue.Values.AddRange(partsValues);
            contentStruct.Fields.Add("parts", partsListValue);

            // 4.3. Tạo 'instance' struct
            var instanceStruct = new Google.Protobuf.WellKnownTypes.Struct();
            instanceStruct.Fields.Add("content", Google.Protobuf.WellKnownTypes.Value.ForStruct(contentStruct));

            // 4.4. Tạo 'instance' Value
            var instanceValue = Google.Protobuf.WellKnownTypes.Value.ForStruct(instanceStruct);

            // 4.5. Tạo request
            var request = new PredictRequest
            {
                Endpoint = EndpointName.FromProjectLocationPublisherModel(
                    _projectId, _location, "google", _model
                ).ToString(),
            };

            request.Instances.Add(instanceValue);

            return request;
        }

        // === HELPER 2: ĐỌC KẾT QUẢ (DÙNG CHUNG) ===
        private string ParseResponse(PredictResponse response)
        {
            try
            {
                var prediction = response.Predictions.First();
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

    // Helper class (Giữ nguyên)
    public static class PartExtensions
    {
        public static Google.Protobuf.WellKnownTypes.Value ToValue(this Part part)
        {
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