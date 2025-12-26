// TUYỆT ĐỐI KHÔNG: using System.Transactions;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using DataLayer.Repositories.Abstraction;
using TransactionEntity = DataLayer.Entities.Transaction;
using DataLayer.Entities;
using DataLayer.Enum;
using DataLayer.Repositories.GenericType;

namespace DataLayer.Repositories
{
    public class TransactionRepository : GenericRepository<TransactionEntity>, ITransactionRepository
    {
        private readonly TpeduContext _context;

        public TransactionRepository(TpeduContext context) : base(context)
        {
            _context = context;
        }

        public async Task<(IEnumerable<TransactionEntity> items, int total)>
            GetByWalletIdAsync(string walletId, int page, int size, CancellationToken ct = default)
        {
            var query = _context.Transactions.AsNoTracking()
                         .Where(t => t.WalletId == walletId)
                         .OrderByDescending(t => t.CreatedAt); // CreatedAt từ BaseEntity

            var total = await query.CountAsync(ct);
            var items = await query.Skip((page - 1) * size).Take(size).ToListAsync(ct);
            return (items, total);
        }

        public Task AddAsync(TransactionEntity entity, CancellationToken ct = default)
            => _context.Transactions.AddAsync(entity, ct).AsTask(); // 👈 AddAsync cụ thể

        public async Task<(IEnumerable<TransactionEntity> items, int total)> GetTransactionsForAdminAsync(
            string? role,
            TransactionType? type,
            TransactionStatus? status,
            DateTime? startDate,
            DateTime? endDate,
            int page,
            int pageSize,
            CancellationToken ct = default)
        {
            var query = _context.Transactions
                .AsNoTracking()
                .Include(t => t.Wallet)
                    .ThenInclude(w => w!.User)
                .AsQueryable();

            // Filter by role
            if (!string.IsNullOrEmpty(role))
            {
                query = query.Where(t => t.Wallet != null && t.Wallet.User != null && t.Wallet.User.RoleName == role);
            }

            // Filter by type
            if (type.HasValue)
            {
                query = query.Where(t => t.Type == type.Value);
            }

            // Filter by status
            if (status.HasValue)
            {
                query = query.Where(t => t.Status == status.Value);
            }

            // Filter by date range
            if (startDate.HasValue)
            {
                query = query.Where(t => t.CreatedAt >= startDate.Value);
            }

            if (endDate.HasValue)
            {
                query = query.Where(t => t.CreatedAt <= endDate.Value.AddDays(1).AddTicks(-1)); // End of day
            }

            var total = await query.CountAsync(ct);
            var items = await query
                .OrderByDescending(t => t.CreatedAt)
                .Skip((page - 1) * pageSize)
                .Take(pageSize)
                .ToListAsync(ct);

            return (items, total);
        }
    }
}
