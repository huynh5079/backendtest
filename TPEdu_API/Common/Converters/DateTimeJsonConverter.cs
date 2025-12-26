using System;
using System.Text.Json;
using System.Text.Json.Serialization;
using BusinessLayer.Helper;

namespace TPEdu_API.Common.Converters
{
    /// <summary>
    /// Custom DateTime converter để serialize DateTime với Vietnam timezone (UTC+7)
    /// Backend lưu DateTime ở Vietnam time, nên khi serialize cần giữ nguyên timezone
    /// </summary>
    public class DateTimeJsonConverter : JsonConverter<DateTime>
    {
        public override DateTime Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            var value = reader.GetString();
            if (string.IsNullOrWhiteSpace(value))
            {
                throw new JsonException("DateTime value cannot be null or empty");
            }

            // Parse ISO 8601 string
            if (DateTime.TryParse(value, out var result))
            {
                // Nếu string có 'Z' (UTC), convert về Vietnam time
                if (value.EndsWith("Z") || value.EndsWith("z"))
                {
                    return DateTimeHelper.ToVietnamTime(result.ToUniversalTime());
                }
                // Nếu không có timezone info, coi như đã là Vietnam time
                return result;
            }

            throw new JsonException($"Invalid DateTime format: {value}");
        }

        public override void Write(Utf8JsonWriter writer, DateTime value, JsonSerializerOptions options)
        {
            // Backend lưu DateTime ở Vietnam time (UTC+7)
            // Một số service đã convert UTC → Vietnam time trước khi trả về
            // Serialize với timezone offset +07:00 để frontend biết đây là Vietnam time
            // Frontend sẽ parse đúng và KHÔNG cộng thêm 7 giờ nữa
            
            DateTime vietnamTime;
            if (value.Kind == DateTimeKind.Utc)
            {
                // Nếu là UTC, convert về Vietnam time
                vietnamTime = DateTimeHelper.ToVietnamTime(value);
            }
            else
            {
                // Nếu đã là Vietnam time (Kind = Unspecified hoặc Local), dùng trực tiếp
                vietnamTime = value;
            }
            
            // Serialize với timezone offset +07:00 để frontend biết đây là Vietnam time
            // Format: "2024-01-01T10:00:00.000+07:00"
            writer.WriteStringValue(vietnamTime.ToString("yyyy-MM-ddTHH:mm:ss.fff+07:00"));
        }
    }

    public class NullableDateTimeJsonConverter : JsonConverter<DateTime?>
    {
        public override DateTime? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            var value = reader.GetString();
            if (string.IsNullOrWhiteSpace(value))
            {
                return null;
            }

            // Parse ISO 8601 string
            if (DateTime.TryParse(value, out var result))
            {
                // Nếu string có 'Z' (UTC), convert về Vietnam time
                if (value.EndsWith("Z") || value.EndsWith("z"))
                {
                    return DateTimeHelper.ToVietnamTime(result.ToUniversalTime());
                }
                // Nếu không có timezone info, coi như đã là Vietnam time
                return result;
            }

            throw new JsonException($"Invalid DateTime format: {value}");
        }

        public override void Write(Utf8JsonWriter writer, DateTime? value, JsonSerializerOptions options)
        {
            if (value.HasValue)
            {
                DateTime vietnamTime;
                if (value.Value.Kind == DateTimeKind.Utc)
                {
                    // Nếu là UTC, convert về Vietnam time
                    vietnamTime = DateTimeHelper.ToVietnamTime(value.Value);
                }
                else
                {
                    // Nếu đã là Vietnam time (Kind = Unspecified hoặc Local), dùng trực tiếp
                    vietnamTime = value.Value;
                }
                
                // Serialize với timezone offset +07:00 để frontend biết đây là Vietnam time
                // Format: "2024-01-01T10:00:00.000+07:00"
                writer.WriteStringValue(vietnamTime.ToString("yyyy-MM-ddTHH:mm:ss.fff+07:00"));
            }
            else
            {
                writer.WriteNullValue();
            }
        }
    }
}

