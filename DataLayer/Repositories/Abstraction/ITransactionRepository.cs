using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using DataLayer.Enum;
using DataLayer.Repositories.GenericType.Abstraction;
using TransactionEntity = DataLayer.Entities.Transaction;

namespace DataLayer.Repositories.Abstraction
{
    public interface ITransactionRepository : IGenericRepository<TransactionEntity>
    {
        Task<(IEnumerable<TransactionEntity> items, int total)>
            GetByWalletIdAsync(string walletId, int page, int size, CancellationToken ct = default);

        Task AddAsync(TransactionEntity entity, CancellationToken ct = default); // 👈 thêm

        Task<(IEnumerable<TransactionEntity> items, int total)> GetTransactionsForAdminAsync(
            string? role,
            TransactionType? type,
            TransactionStatus? status,
            DateTime? startDate,
            DateTime? endDate,
            int page,
            int pageSize,
            CancellationToken ct = default);
    }
}
