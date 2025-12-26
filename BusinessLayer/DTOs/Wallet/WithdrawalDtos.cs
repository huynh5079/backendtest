using DataLayer.Enum;

namespace BusinessLayer.DTOs.Wallet;

public class CreateWithdrawalRequestDto
{
    public decimal Amount { get; set; }
    public WithdrawalMethod Method { get; set; }
    public string RecipientInfo { get; set; } = default!; // Số điện thoại MoMo hoặc thông tin khác
    public string? RecipientName { get; set; }
    public string? Note { get; set; }
}

public class WithdrawalRequestDto
{
    public string Id { get; set; } = default!;
    public string UserId { get; set; } = default!;
    public string? UserName { get; set; }
    public decimal Amount { get; set; }
    public string Method { get; set; } = default!;
    public string Status { get; set; } = default!;
    public string RecipientInfo { get; set; } = default!;
    public string? RecipientName { get; set; }
    public string? Note { get; set; }
    public string? AdminNote { get; set; }
    public string? ProcessedByUserId { get; set; }
    public string? ProcessedByUserName { get; set; }
    public DateTime? ProcessedAt { get; set; }
    public string? PaymentId { get; set; }
    public string? TransactionId { get; set; }
    public string? FailureReason { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

public class ApproveWithdrawalRequestDto
{
    public string? AdminNote { get; set; }
}

public class RejectWithdrawalRequestDto
{
    public string Reason { get; set; } = default!;
    public string? AdminNote { get; set; }
}

