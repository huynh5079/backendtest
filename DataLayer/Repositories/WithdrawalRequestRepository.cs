using DataLayer.Entities;
using DataLayer.Repositories.Abstraction;
using DataLayer.Repositories.GenericType;

namespace DataLayer.Repositories;

public class WithdrawalRequestRepository : GenericRepository<WithdrawalRequest>, IWithdrawalRequestRepository
{
    public WithdrawalRequestRepository(TpeduContext context) : base(context)
    {
    }
}

