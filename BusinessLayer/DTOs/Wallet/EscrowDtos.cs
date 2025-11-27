using System;

namespace BusinessLayer.DTOs.Wallet
{
    public class PayEscrowRequest
    {
        public string ClassId { get; set; } = default!;
        // GrossAmount và CommissionRate đã bị loại bỏ - lấy từ DB để bảo mật
        // GrossAmount sẽ lấy từ Class.Price trong database
        // CommissionRate sẽ tự động tính từ Commission rules
        public string? PayerStudentUserId { get; set; } // nếu parent trả thay
    }

    public class ReleaseEscrowRequest
    {
        public string EscrowId { get; set; } = default!;
    }

    public class RefundEscrowRequest
    {
        public string EscrowId { get; set; } = default!;
    }

    public class ProcessTutorDepositRequest
    {
        public string ClassId { get; set; } = default!;
        // DepositAmount đã bị loại bỏ - gia sư không thể tự chỉnh, luôn dùng giá trị mặc định từ config
    }

    public class ForfeitDepositRequest
    {
        public string TutorDepositEscrowId { get; set; } = default!;
        public string Reason { get; set; } = default!;
        public bool RefundToStudent { get; set; } = true; // true: trả về student, false: giữ lại cho system
    }

    public class SystemSettingsDto
    {
        public string Id { get; set; } = default!;
        public decimal DepositRate { get; set; } // Tỷ lệ % (ví dụ: 0.10 = 10%)
        public bool IsActive { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime UpdatedAt { get; set; }
    }

    public class UpdateDepositSettingsDto
    {
        public decimal? DepositRate { get; set; } // Optional: Tỷ lệ % (ví dụ: 0.10 = 10%)
    }
}


