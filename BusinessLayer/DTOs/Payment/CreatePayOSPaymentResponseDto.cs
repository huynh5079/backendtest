using System.Text.Json.Serialization;

namespace BusinessLayer.DTOs.Payment;

public class CreatePayOSPaymentResponseDto
{
    [JsonPropertyName("code")]
    public string Code { get; set; } = default!;
    
    [JsonPropertyName("desc")]
    public string Desc { get; set; } = default!;
    
    [JsonPropertyName("data")]
    public PayOSPaymentData? Data { get; set; }
    
    // Custom fields (not from PayOS API)
    public string PaymentId { get; set; } = default!;
    public string Provider { get; set; } = "PayOS";
}

public class PayOSPaymentData
{
    [JsonPropertyName("bin")]
    public string? Bin { get; set; }
    
    [JsonPropertyName("accountNumber")]
    public string? AccountNumber { get; set; }
    
    [JsonPropertyName("accountName")]
    public string? AccountName { get; set; }
    
    [JsonPropertyName("amount")]
    public decimal Amount { get; set; }
    
    [JsonPropertyName("description")]
    public string Description { get; set; } = default!;
    
    [JsonPropertyName("orderCode")]
    public int OrderCode { get; set; }
    
    [JsonPropertyName("currency")]
    public string Currency { get; set; } = "VND";
    
    [JsonPropertyName("paymentLinkId")]
    public string? PaymentLinkId { get; set; }
    
    [JsonPropertyName("status")]
    public string? Status { get; set; }
    
    [JsonPropertyName("expiredAt")]
    public long? ExpiredAt { get; set; }
    
    [JsonPropertyName("checkoutUrl")]
    public string? CheckoutUrl { get; set; }
    
    [JsonPropertyName("qrCode")]
    public string? QrCode { get; set; }
}

