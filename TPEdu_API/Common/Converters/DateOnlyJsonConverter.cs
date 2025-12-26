using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TPEdu_API.Common.Converters
{
    public class DateOnlyJsonConverter : JsonConverter<DateOnly>
    {
        private static readonly string[] AcceptedFormats = new[]
        {
            // Standard formats with zero-padding
            "yyyy-MM-dd",
            "yyyy/MM/dd",
            "dd-MM-yyyy",
            "dd/MM/yyyy",
            "MM-dd-yyyy",
            "MM/dd/yyyy",
            // Flexible formats (single or double digit)
            "d-M-yyyy",
            "d/M/yyyy",
            "M-d-yyyy",
            "M/d/yyyy",
            "yyyy-M-d",
            "yyyy/M/d"
        };

        public override DateOnly Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            var value = reader.GetString()?.Trim();
            
            if (string.IsNullOrWhiteSpace(value))
            {
                throw new JsonException("DateOnly value cannot be null or empty");
            }

            // Try parsing with multiple formats
            if (DateOnly.TryParseExact(value, AcceptedFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var result))
            {
                return result;
            }

            // Also try general parse as fallback
            if (DateOnly.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out result))
            {
                return result;
            }

            throw new JsonException($"Invalid DateOnly format. Received: '{value}'. Expected formats: yyyy-MM-dd, yyyy/MM/dd, dd-MM-yyyy, dd/MM/yyyy, etc.");
        }

        public override void Write(Utf8JsonWriter writer, DateOnly value, JsonSerializerOptions options)
        {
            writer.WriteStringValue(value.ToString("yyyy-MM-dd"));
        }
    }

    public class NullableDateOnlyJsonConverter : JsonConverter<DateOnly?>
    {
        private static readonly string[] AcceptedFormats = new[]
        {
            // Standard formats with zero-padding
            "yyyy-MM-dd",
            "yyyy/MM/dd",
            "dd-MM-yyyy",
            "dd/MM/yyyy",
            "MM-dd-yyyy",
            "MM/dd/yyyy",
            // Flexible formats (single or double digit)
            "d-M-yyyy",
            "d/M/yyyy",
            "M-d-yyyy",
            "M/d/yyyy",
            "yyyy-M-d",
            "yyyy/M/d"
        };

        public override DateOnly? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            var value = reader.GetString()?.Trim();
            
            if (string.IsNullOrWhiteSpace(value))
            {
                return null;
            }

            // Try parsing with multiple formats
            if (DateOnly.TryParseExact(value, AcceptedFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var result))
            {
                return result;
            }

            // Also try general parse as fallback
            if (DateOnly.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out result))
            {
                return result;
            }

            throw new JsonException($"Invalid DateOnly format. Received: '{value}'. Expected formats: yyyy-MM-dd, yyyy/MM/dd, dd-MM-yyyy, dd/MM/yyyy, etc.");
        }

        public override void Write(Utf8JsonWriter writer, DateOnly? value, JsonSerializerOptions options)
        {
            if (value.HasValue)
            {
                writer.WriteStringValue(value.Value.ToString("yyyy-MM-dd"));
            }
            else
            {
                writer.WriteNullValue();
            }
        }
    }
}
