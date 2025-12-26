using DataLayer.Enum;
using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace BusinessLayer.DTOs.Payment;

public class CreatePayOSPaymentRequestDto
{
    [Range(1, long.MaxValue)]
    [JsonPropertyName("amount")]
    public decimal Amount { get; set; }

    [Required]
    [JsonPropertyName("contextType")]
    public PaymentContextType ContextType { get; set; }

    /// <summary>
    /// ContextId is required for Escrow, optional for WalletDeposit (will use userId if not provided)
    /// </summary>
    [JsonPropertyName("contextId")]
    public string? ContextId { get; set; }

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [JsonPropertyName("extraData")]
    public string? ExtraData { get; set; }
}

