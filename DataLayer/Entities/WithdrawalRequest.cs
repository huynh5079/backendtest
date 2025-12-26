using DataLayer.Enum;

namespace DataLayer.Entities
{
    /// <summary>
    /// Yêu cầu rút tiền từ ví của user
    /// </summary>
    public partial class WithdrawalRequest : BaseEntity
    {
        public string UserId { get; set; } = default!;
        
        public decimal Amount { get; set; }
        
        public WithdrawalMethod Method { get; set; }
        
        public WithdrawalStatus Status { get; set; } = WithdrawalStatus.Pending;
        
        // Thông tin nhận tiền (tùy theo Method)
        // Nếu Method = MoMo: lưu số điện thoại MoMo
        // Nếu Method = BankTransfer: lưu thông tin ngân hàng (sẽ mở rộng sau)
        public string RecipientInfo { get; set; } = default!; // Số điện thoại MoMo hoặc thông tin khác
        
        public string? RecipientName { get; set; } // Tên người nhận (optional)
        
        public string? Note { get; set; } // Ghi chú từ user
        
        public string? AdminNote { get; set; } // Ghi chú từ admin khi duyệt/từ chối
        
        public string? ProcessedByUserId { get; set; } // Admin đã xử lý
        
        public DateTime? ProcessedAt { get; set; } // Thời gian xử lý
        
        public string? PaymentId { get; set; } // ID của payment từ MoMo (nếu dùng MoMo API)
        
        public string? TransactionId { get; set; } // Transaction ID từ MoMo (nếu dùng MoMo API)
        
        public string? FailureReason { get; set; } // Lý do thất bại (nếu Status = Failed)
        
        // Navigation properties
        public virtual User? User { get; set; }
        public virtual User? ProcessedByUser { get; set; }
    }
}

