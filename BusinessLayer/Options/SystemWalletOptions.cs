namespace BusinessLayer.Options;

public class SystemWalletOptions
{
    public string SystemWalletUserId { get; set; } = default!;
    
    // DefaultDepositAmount đã được chuyển sang database (SystemSettings entity)
    // Admin có thể cập nhật qua API: PUT /admin/system-settings/deposit-amount
    // Không còn hard-code ở đây nữa
}

