using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using BusinessLayer.DTOs.Wallet;
using DataLayer.Entities;
using DataLayer.Enum;

namespace BusinessLayer.Service.Interface
{
    public interface IWalletService
    {
        // Khớp với controller: GET api/Wallet/me
        Task<Wallet> GetMyWalletAsync(string userId, CancellationToken ct = default);

        // Khớp với controller: GET api/Wallet/me/transactions
        Task<(IEnumerable<Transaction> items, int total)>
            GetMyTransactionsAsync(string userId, int pageNumber, int pageSize, CancellationToken ct = default);

        // Khớp với controller: POST deposit/withdraw/transfer (trả OperationResult)
        Task<OperationResult> DepositAsync(string userId, decimal amount, string? note, CancellationToken ct = default);
        Task<OperationResult> WithdrawAsync(string userId, decimal amount, string? note, CancellationToken ct = default);
        Task<OperationResult> TransferAsync(string fromUserId, string toUserId, decimal amount, string? note, CancellationToken ct = default);

        // (Tuỳ chọn) Parent nạp cho Student
        Task<OperationResult> DepositForStudentAsync(string payerUserId, string studentUserId, decimal amount, string? note, CancellationToken ct = default);

        // User chuyển tiền của chính họ vào ví admin (dùng cho Student/Parent/Tutor)
        // API này tự động lấy SystemWalletUserId từ config, không cần userId của admin
        Task<OperationResult> TransferToAdminAsync(string userId, decimal amount, string? note, CancellationToken ct = default);

        // Lấy username từ userId
        Task<string?> GetUsernameByUserIdAsync(string userId, CancellationToken ct = default);

        // Admin: Lấy transactions theo role, type, status, date range
        Task<(IEnumerable<Transaction> items, int total)> GetTransactionsForAdminAsync(
            string? role,
            TransactionType? type,
            TransactionStatus? status,
            DateTime? startDate,
            DateTime? endDate,
            int page,
            int pageSize,
            CancellationToken ct = default);

        // Admin: Lấy transaction detail by id
        Task<Transaction?> GetTransactionDetailForAdminAsync(string transactionId, CancellationToken ct = default);

        // Unsave changes wallet for schedule rollback
        Task ProcessPaymentActionAsync(string userId, decimal amount, string description, CancellationToken ct = default);
    }
}
