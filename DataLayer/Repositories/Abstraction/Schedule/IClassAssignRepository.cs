using DataLayer.Entities;
using DataLayer.Repositories.GenericType.Abstraction;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace DataLayer.Repositories.Abstraction.Schedule
{
    public interface IClassAssignRepository : IGenericRepository<ClassAssign>
    {
        Task<bool> IsApprovedAsync(string classId, string studentProfileId);
        Task<bool> IsAnyChildApprovedAsync(string classId, List<string> studentProfileIds);
    }
}
