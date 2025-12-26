using BusinessLayer.DTOs.Wallet;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace BusinessLayer.Service.Interface;

public interface IWithdrawalService
{
    /// <summary>
    /// User tạo yêu cầu rút tiền
    /// </summary>
    Task<OperationResult> CreateWithdrawalRequestAsync(string userId, CreateWithdrawalRequestDto dto, CancellationToken ct = default);
    
    /// <summary>
    /// User xem danh sách yêu cầu rút tiền của mình
    /// </summary>
    Task<(IEnumerable<WithdrawalRequestDto> items, int total)> GetMyWithdrawalRequestsAsync(
        string userId, 
        int pageNumber, 
        int pageSize, 
        CancellationToken ct = default);
    
    /// <summary>
    /// User xem chi tiết yêu cầu rút tiền của mình
    /// </summary>
    Task<WithdrawalRequestDto?> GetMyWithdrawalRequestByIdAsync(string userId, string requestId, CancellationToken ct = default);
    
    /// <summary>
    /// User hủy yêu cầu rút tiền (chỉ khi Status = Pending)
    /// </summary>
    Task<OperationResult> CancelWithdrawalRequestAsync(string userId, string requestId, CancellationToken ct = default);
    
    /// <summary>
    /// Admin duyệt yêu cầu rút tiền và xử lý chuyển tiền
    /// </summary>
    Task<OperationResult> ApproveWithdrawalRequestAsync(string adminUserId, string requestId, ApproveWithdrawalRequestDto dto, CancellationToken ct = default);
    
    /// <summary>
    /// Admin từ chối yêu cầu rút tiền
    /// </summary>
    Task<OperationResult> RejectWithdrawalRequestAsync(string adminUserId, string requestId, RejectWithdrawalRequestDto dto, CancellationToken ct = default);
    
    /// <summary>
    /// Admin xem danh sách tất cả yêu cầu rút tiền
    /// </summary>
    Task<(IEnumerable<WithdrawalRequestDto> items, int total)> GetAllWithdrawalRequestsAsync(
        string? status,
        int pageNumber,
        int pageSize,
        CancellationToken ct = default);
    
    /// <summary>
    /// Admin xem chi tiết yêu cầu rút tiền
    /// </summary>
    Task<WithdrawalRequestDto?> GetWithdrawalRequestByIdAsync(string requestId, CancellationToken ct = default);
}

