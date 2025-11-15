using DataLayer.Entities;
using DataLayer.Enum;
using DataLayer.Repositories.Abstraction.Schedule;
using DataLayer.Repositories.GenericType;
using Microsoft.EntityFrameworkCore;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace DataLayer.Repositories.Schedule
{
    public class ClassAssignRepository : GenericRepository<ClassAssign>, IClassAssignRepository
    {
        public ClassAssignRepository(TpeduContext context) : base(context)
        {
            // Chỉ cần gọi constructor của base
        }
        // Che hàm CreateAsync

        public new async Task CreateAsync(ClassAssign entity)
        {
            await _dbSet.AddAsync(entity);
            // KHÔNG GỌI SaveChangesAsync()
        }

        // Che hàm UpdateAsync của base
        public new Task UpdateAsync(ClassAssign entity)
        {
            _dbSet.Attach(entity);
            _context.Entry(entity).State = EntityState.Modified;
            return Task.CompletedTask;
            // KHÔNG GỌI SaveChangesAsync()
        }

        public async Task<bool> IsApprovedAsync(string classId, string studentProfileId)
        {
            return await _dbSet
                .AsNoTracking()
                .AnyAsync(ca => ca.ClassId == classId
                             && ca.StudentId == studentProfileId
                             && ca.ApprovalStatus == ApprovalStatus.Approved);
        }

        public async Task<bool> IsAnyChildApprovedAsync(string classId, List<string> studentProfileIds)
        {
            // _dbSet ở đây là IQueryable<ClassAssign> nên có thể dùng AnyAsync
            return await _dbSet
                .AsNoTracking()
                .AnyAsync(ca => ca.ClassId == classId
                             && ca.StudentId != null
                             && studentProfileIds.Contains(ca.StudentId) // Kiểm tra xem có bất kỳ ID con nào trong danh sách
                             && ca.ApprovalStatus == ApprovalStatus.Approved);
        }
    }
}
