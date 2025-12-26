using BusinessLayer.DTOs.Wallet;
using BusinessLayer.Helper;
using BusinessLayer.Service.Interface;
using DataLayer.Entities;
using DataLayer.Enum;
using Microsoft.EntityFrameworkCore;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace BusinessLayer.Service;

public class WithdrawalService : IWithdrawalService
{
    private readonly TpeduContext _context;
    private readonly IWalletService _walletService;
    private readonly INotificationService _notificationService;
    private readonly IMomoPaymentService? _momoPaymentService; // Optional, để tích hợp MoMo sau

    public WithdrawalService(
        TpeduContext context,
        IWalletService walletService,
        INotificationService notificationService,
        IMomoPaymentService? momoPaymentService = null)
    {
        _context = context;
        _walletService = walletService;
        _notificationService = notificationService;
        _momoPaymentService = momoPaymentService;
    }

    public async Task<OperationResult> CreateWithdrawalRequestAsync(string userId, CreateWithdrawalRequestDto dto, CancellationToken ct = default)
    {
        // Validation
        if (dto.Amount <= 0)
            return new OperationResult { Status = "Fail", Message = "Số tiền rút phải lớn hơn 0" };

        const decimal minWithdrawAmount = 10000m;
        if (dto.Amount < minWithdrawAmount)
            return new OperationResult { Status = "Fail", Message = $"Số tiền rút tối thiểu là {minWithdrawAmount:N0} VND" };

        if (string.IsNullOrWhiteSpace(dto.RecipientInfo))
            return new OperationResult { Status = "Fail", Message = "Vui lòng nhập thông tin nhận tiền (số điện thoại MoMo hoặc thông tin khác)" };

        // Validate method
        if (dto.Method == WithdrawalMethod.MoMo)
        {
            // Validate phone number format (basic)
            var phone = dto.RecipientInfo.Trim();
            if (!phone.StartsWith("0") || phone.Length != 10)
                return new OperationResult { Status = "Fail", Message = "Số điện thoại MoMo không hợp lệ. Vui lòng nhập số điện thoại 10 chữ số bắt đầu bằng 0" };
        }

        // Check wallet balance
        var wallet = await _walletService.GetMyWalletAsync(userId, ct);
        if (wallet.IsFrozen)
            return new OperationResult { Status = "Fail", Message = "Ví của bạn đã bị khóa. Vui lòng liên hệ admin để được hỗ trợ." };

        if (wallet.Balance < dto.Amount)
            return new OperationResult { Status = "Fail", Message = $"Số dư không đủ. Số dư hiện tại: {wallet.Balance:N0} VND, Số tiền muốn rút: {dto.Amount:N0} VND" };

        // Use transaction to ensure atomicity
        using var tx = await _context.Database.BeginTransactionAsync(ct);
        try
        {
            // Create withdrawal request - Tự động hoàn thành ngay, không cần admin phê duyệt
            var request = new WithdrawalRequest
            {
                Id = Guid.NewGuid().ToString(),
                UserId = userId,
                Amount = dto.Amount,
                Method = dto.Method,
                Status = WithdrawalStatus.Completed, // Tự động hoàn thành ngay, không cần admin phê duyệt
                RecipientInfo = dto.RecipientInfo.Trim(),
                RecipientName = dto.RecipientName?.Trim(),
                Note = dto.Note?.Trim(),
                ProcessedAt = DateTimeHelper.VietnamNow, // Xử lý ngay lập tức
                CreatedAt = DateTimeHelper.VietnamNow,
                UpdatedAt = DateTimeHelper.VietnamNow
            };

            _context.WithdrawalRequests.Add(request);

            // Deduct wallet balance immediately
            var trackedWallet = await _context.Wallets.FindAsync(new object[] { wallet.Id }, ct);
            if (trackedWallet == null)
            {
                await tx.RollbackAsync(ct);
                return new OperationResult { Status = "Fail", Message = "Không tìm thấy ví người dùng" };
            }

            trackedWallet.Balance -= dto.Amount;
            _context.Wallets.Update(trackedWallet);

            // Create transaction record
            var transaction = new Transaction
            {
                Id = Guid.NewGuid().ToString(),
                WalletId = wallet.Id,
                Type = TransactionType.Debit,
                Status = TransactionStatus.Succeeded,
                Amount = dto.Amount,
                Note = $"Rút tiền qua {dto.Method} - {dto.RecipientInfo.Trim()}",
                CreatedAt = DateTimeHelper.VietnamNow,
                UpdatedAt = DateTimeHelper.VietnamNow
            };
            await _context.Transactions.AddAsync(transaction, ct);

            // Update withdrawal request with transaction ID
            request.TransactionId = transaction.Id;

            await _context.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);

            // Send notification to user
            try
            {
                await _notificationService.CreateAccountNotificationAsync(
                    userId,
                    NotificationType.WalletWithdraw,
                    $"Rút tiền thành công {dto.Amount:N0} VND về {dto.Method} ({dto.RecipientInfo.Trim()}).",
                    request.Id,
                    ct);
                await _context.SaveChangesAsync(ct);
            }
            catch (Exception ex)
            {
                // Log nhưng không fail request
                Console.WriteLine($"Error sending notification: {ex.Message}");
            }

            return new OperationResult 
            { 
                Status = "Ok", 
                Message = $"Rút tiền thành công {dto.Amount:N0} VND về {dto.Method}.",
                Data = new { RequestId = request.Id, TransactionId = transaction.Id }
            };
        }
        catch (Exception ex)
        {
            await tx.RollbackAsync(ct);
            Console.WriteLine($"Error processing withdrawal: {ex.Message}");
            return new OperationResult { Status = "Fail", Message = $"Lỗi xử lý rút tiền: {ex.Message}" };
        }
    }

    public async Task<(IEnumerable<WithdrawalRequestDto> items, int total)> GetMyWithdrawalRequestsAsync(
        string userId, int pageNumber, int pageSize, CancellationToken ct = default)
    {
        var query = _context.WithdrawalRequests
            .AsNoTracking()
            .Include(w => w.ProcessedByUser)
            .Where(w => w.UserId == userId)
            .OrderByDescending(w => w.CreatedAt);

        var total = await query.CountAsync(ct);
        var items = await query
            .Skip((pageNumber - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(ct);

        var dtos = items.Select(w => new WithdrawalRequestDto
        {
            Id = w.Id,
            UserId = w.UserId,
            Amount = w.Amount,
            Method = w.Method.ToString(),
            Status = w.Status.ToString(),
            RecipientInfo = w.RecipientInfo,
            RecipientName = w.RecipientName,
            Note = w.Note,
            AdminNote = w.AdminNote,
            ProcessedByUserId = w.ProcessedByUserId,
            ProcessedByUserName = w.ProcessedByUser?.UserName,
            ProcessedAt = w.ProcessedAt,
            PaymentId = w.PaymentId,
            TransactionId = w.TransactionId,
            FailureReason = w.FailureReason,
            CreatedAt = w.CreatedAt,
            UpdatedAt = w.UpdatedAt
        }).ToList();

        return (dtos, total);
    }

    public async Task<WithdrawalRequestDto?> GetMyWithdrawalRequestByIdAsync(string userId, string requestId, CancellationToken ct = default)
    {
        var request = await _context.WithdrawalRequests
            .AsNoTracking()
            .Include(w => w.ProcessedByUser)
            .FirstOrDefaultAsync(w => w.Id == requestId && w.UserId == userId, ct);

        if (request == null) return null;

        return new WithdrawalRequestDto
        {
            Id = request.Id,
            UserId = request.UserId,
            Amount = request.Amount,
            Method = request.Method.ToString(),
            Status = request.Status.ToString(),
            RecipientInfo = request.RecipientInfo,
            RecipientName = request.RecipientName,
            Note = request.Note,
            AdminNote = request.AdminNote,
            ProcessedByUserId = request.ProcessedByUserId,
            ProcessedByUserName = request.ProcessedByUser?.UserName,
            ProcessedAt = request.ProcessedAt,
            PaymentId = request.PaymentId,
            TransactionId = request.TransactionId,
            FailureReason = request.FailureReason,
            CreatedAt = request.CreatedAt,
            UpdatedAt = request.UpdatedAt
        };
    }

    public async Task<OperationResult> CancelWithdrawalRequestAsync(string userId, string requestId, CancellationToken ct = default)
    {
        var request = await _context.WithdrawalRequests
            .FirstOrDefaultAsync(w => w.Id == requestId && w.UserId == userId, ct);

        if (request == null)
            return new OperationResult { Status = "Fail", Message = "Không tìm thấy yêu cầu rút tiền" };

        if (request.Status != WithdrawalStatus.Pending)
            return new OperationResult { Status = "Fail", Message = $"Không thể hủy yêu cầu đang ở trạng thái: {request.Status}" };

        request.Status = WithdrawalStatus.Cancelled;
        request.UpdatedAt = DateTimeHelper.VietnamNow;
        await _context.SaveChangesAsync(ct);

        return new OperationResult { Status = "Ok", Message = "Đã hủy yêu cầu rút tiền thành công" };
    }

    public async Task<OperationResult> ApproveWithdrawalRequestAsync(string adminUserId, string requestId, ApproveWithdrawalRequestDto dto, CancellationToken ct = default)
    {
        using var tx = await _context.Database.BeginTransactionAsync(ct);

        try
        {
            var request = await _context.WithdrawalRequests
                .FirstOrDefaultAsync(w => w.Id == requestId, ct);

            if (request == null)
                return new OperationResult { Status = "Fail", Message = "Không tìm thấy yêu cầu rút tiền" };

            if (request.Status != WithdrawalStatus.Pending)
                return new OperationResult { Status = "Fail", Message = $"Yêu cầu đang ở trạng thái: {request.Status}, không thể duyệt" };

            // Check wallet balance again
            var wallet = await _walletService.GetMyWalletAsync(request.UserId, ct);
            if (wallet.IsFrozen)
                return new OperationResult { Status = "Fail", Message = "Ví của người dùng đã bị khóa" };

            if (wallet.Balance < request.Amount)
                return new OperationResult { Status = "Fail", Message = $"Số dư không đủ. Số dư hiện tại: {wallet.Balance:N0} VND" };

            // Update request status
            request.Status = WithdrawalStatus.Approved;
            request.ProcessedByUserId = adminUserId;
            request.ProcessedAt = DateTimeHelper.VietnamNow;
            request.AdminNote = dto.AdminNote?.Trim();
            request.UpdatedAt = DateTimeHelper.VietnamNow;

            // Trừ tiền từ ví user
            // Reload wallet từ context để đảm bảo được track
            var trackedWallet = await _context.Wallets.FindAsync(new object[] { wallet.Id }, ct);
            if (trackedWallet == null)
                return new OperationResult { Status = "Fail", Message = "Không tìm thấy ví người dùng" };
            
            trackedWallet.Balance -= request.Amount;
            _context.Wallets.Update(trackedWallet);

            // Tạo transaction record
            var transaction = new Transaction
            {
                Id = Guid.NewGuid().ToString(),
                WalletId = wallet.Id,
                Type = TransactionType.Debit,
                Status = TransactionStatus.Succeeded,
                Amount = request.Amount,
                Note = $"Rút tiền qua {request.Method} - {request.RecipientInfo}",
                CreatedAt = DateTimeHelper.VietnamNow,
                UpdatedAt = DateTimeHelper.VietnamNow
            };
            await _context.Transactions.AddAsync(transaction, ct);

            // Xử lý chuyển tiền
            // TODO: Tích hợp MoMo Payout API khi có
            // Hiện tại: Đánh dấu là Processing, admin sẽ xử lý thủ công hoặc tích hợp API sau
            if (request.Method == WithdrawalMethod.MoMo)
            {
                request.Status = WithdrawalStatus.Processing;
                // TODO: Gọi MoMo Payout API
                // var momoResult = await ProcessMoMoPayoutAsync(request, ct);
                // if (momoResult.Success)
                // {
                //     request.Status = WithdrawalStatus.Completed;
                //     request.PaymentId = momoResult.PaymentId;
                //     request.TransactionId = momoResult.TransactionId;
                // }
                // else
                // {
                //     request.Status = WithdrawalStatus.Failed;
                //     request.FailureReason = momoResult.ErrorMessage;
                //     // Rollback: Hoàn tiền lại cho user
                //     wallet.Balance += request.Amount;
                // }
            }
            else
            {
                // Các phương thức khác (BankTransfer, PayPal) - sẽ implement sau
                request.Status = WithdrawalStatus.Processing;
            }

            await _context.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);

            // Send notification to user
            try
            {
                await _notificationService.CreateAccountNotificationAsync(
                    request.UserId,
                    NotificationType.WalletWithdraw,
                    $"Yêu cầu rút tiền {request.Amount:N0} VND đã được duyệt và đang được xử lý.",
                    request.Id,
                    ct);
                await _context.SaveChangesAsync(ct);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error sending notification: {ex.Message}");
            }

            return new OperationResult 
            { 
                Status = "Ok", 
                Message = "Đã duyệt yêu cầu rút tiền thành công" 
            };
        }
        catch (Exception ex)
        {
            await tx.RollbackAsync(ct);
            Console.WriteLine($"Error approving withdrawal request: {ex.Message}");
            return new OperationResult { Status = "Fail", Message = $"Lỗi: {ex.Message}" };
        }
    }

    public async Task<OperationResult> RejectWithdrawalRequestAsync(string adminUserId, string requestId, RejectWithdrawalRequestDto dto, CancellationToken ct = default)
    {
        var request = await _context.WithdrawalRequests
            .FirstOrDefaultAsync(w => w.Id == requestId, ct);

        if (request == null)
            return new OperationResult { Status = "Fail", Message = "Không tìm thấy yêu cầu rút tiền" };

        if (request.Status != WithdrawalStatus.Pending)
            return new OperationResult { Status = "Fail", Message = $"Yêu cầu đang ở trạng thái: {request.Status}, không thể từ chối" };

        request.Status = WithdrawalStatus.Rejected;
        request.ProcessedByUserId = adminUserId;
        request.ProcessedAt = DateTimeHelper.VietnamNow;
        request.AdminNote = dto.AdminNote?.Trim();
        request.FailureReason = dto.Reason?.Trim();
        request.UpdatedAt = DateTimeHelper.VietnamNow;

        await _context.SaveChangesAsync(ct);

        // Send notification to user
        try
        {
            await _notificationService.CreateAccountNotificationAsync(
                request.UserId,
                NotificationType.WalletWithdraw,
                $"Yêu cầu rút tiền {request.Amount:N0} VND đã bị từ chối. Lý do: {dto.Reason}",
                request.Id,
                ct);
            await _context.SaveChangesAsync(ct);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error sending notification: {ex.Message}");
        }

        return new OperationResult { Status = "Ok", Message = "Đã từ chối yêu cầu rút tiền" };
    }

    public async Task<(IEnumerable<WithdrawalRequestDto> items, int total)> GetAllWithdrawalRequestsAsync(
        string? status, int pageNumber, int pageSize, CancellationToken ct = default)
    {
        var query = _context.WithdrawalRequests
            .AsNoTracking()
            .Include(w => w.User)
            .Include(w => w.ProcessedByUser)
            .AsQueryable();

        if (!string.IsNullOrWhiteSpace(status) && Enum.TryParse<WithdrawalStatus>(status, out var statusEnum))
        {
            query = query.Where(w => w.Status == statusEnum);
        }

        query = query.OrderByDescending(w => w.CreatedAt);

        var total = await query.CountAsync(ct);
        var items = await query
            .Skip((pageNumber - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(ct);

        var dtos = items.Select(w => new WithdrawalRequestDto
        {
            Id = w.Id,
            UserId = w.UserId,
            UserName = w.User?.UserName,
            Amount = w.Amount,
            Method = w.Method.ToString(),
            Status = w.Status.ToString(),
            RecipientInfo = w.RecipientInfo,
            RecipientName = w.RecipientName,
            Note = w.Note,
            AdminNote = w.AdminNote,
            ProcessedByUserId = w.ProcessedByUserId,
            ProcessedByUserName = w.ProcessedByUser?.UserName,
            ProcessedAt = w.ProcessedAt,
            PaymentId = w.PaymentId,
            TransactionId = w.TransactionId,
            FailureReason = w.FailureReason,
            CreatedAt = w.CreatedAt,
            UpdatedAt = w.UpdatedAt
        }).ToList();

        return (dtos, total);
    }

    public async Task<WithdrawalRequestDto?> GetWithdrawalRequestByIdAsync(string requestId, CancellationToken ct = default)
    {
        var request = await _context.WithdrawalRequests
            .AsNoTracking()
            .Include(w => w.User)
            .Include(w => w.ProcessedByUser)
            .FirstOrDefaultAsync(w => w.Id == requestId, ct);

        if (request == null) return null;

        return new WithdrawalRequestDto
        {
            Id = request.Id,
            UserId = request.UserId,
            UserName = request.User?.UserName,
            Amount = request.Amount,
            Method = request.Method.ToString(),
            Status = request.Status.ToString(),
            RecipientInfo = request.RecipientInfo,
            RecipientName = request.RecipientName,
            Note = request.Note,
            AdminNote = request.AdminNote,
            ProcessedByUserId = request.ProcessedByUserId,
            ProcessedByUserName = request.ProcessedByUser?.UserName,
            ProcessedAt = request.ProcessedAt,
            PaymentId = request.PaymentId,
            TransactionId = request.TransactionId,
            FailureReason = request.FailureReason,
            CreatedAt = request.CreatedAt,
            UpdatedAt = request.UpdatedAt
        };
    }

    // TODO: Implement MoMo Payout API integration
    // private async Task<MoMoPayoutResult> ProcessMoMoPayoutAsync(WithdrawalRequest request, CancellationToken ct)
    // {
    //     // Gọi MoMo API để chuyển tiền
    //     // Return result với PaymentId, TransactionId hoặc error message
    // }
}

