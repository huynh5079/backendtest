using DataLayer.Entities;
using DataLayer.Enum;
using DataLayer.Repositories.GenericType.Abstraction;

namespace DataLayer.Repositories.Abstraction;

public interface IWithdrawalRequestRepository : IGenericRepository<WithdrawalRequest>
{
    // Có thể thêm các methods đặc biệt nếu cần
    // Hiện tại chỉ dùng IGenericRepository là đủ
}

