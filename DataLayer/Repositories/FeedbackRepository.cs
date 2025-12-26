using DataLayer.Entities;
using DataLayer.Enum;
using DataLayer.Repositories.Abstraction;
using DataLayer.Repositories.GenericType;
using Microsoft.EntityFrameworkCore;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace DataLayer.Repositories
{
    public class FeedbackRepository : GenericRepository<Feedback>, IFeedbackRepository
    {
        public FeedbackRepository(TpeduContext ctx) : base(ctx) { }

        public async Task<bool> ExistsAsync(string fromUserId, string toUserId, string classId)
        {
            return await _dbSet.AnyAsync(f =>
                f.FromUserId == fromUserId &&
                f.ToUserId == toUserId &&
                f.ClassId == classId);
        }

        public async Task<List<Feedback>> GetByClassAsync(string classId)
        {
            return await _dbSet
                .Include(f => f.FromUser)
                .Include(f => f.ToUser)
                .Where(f => f.ClassId == classId)
                .OrderByDescending(f => f.CreatedAt)
                .ToListAsync();
        }

        // Chỉ lấy feedback "public" trên trang Tutor (LessonId == null & IsPublicOnTutorProfile == true)
        public async Task<(List<Feedback> items, int total)> GetByTutorUserAsync(string tutorUserId, int page, int pageSize)
        {
            var q = _dbSet
                .Include(f => f.FromUser)
                .Include(f => f.ToUser)
                .Where(f => f.ToUserId == tutorUserId &&
                            f.LessonId == null &&
                            f.IsPublicOnTutorProfile == true)
                .OrderByDescending(f => f.CreatedAt);

            var total = await q.CountAsync();
            var items = await q.Skip((page - 1) * pageSize).Take(pageSize).ToListAsync();
            return (items, total);
        }

        // Tính rating: trung bình theo từng người học (avg per rater) -> rồi trung bình các raters
        // Đếm TẤT CẢ feedbacks có Rating (không kể class status)
        public async Task<(double avg, int count)> CalcTutorRatingAsync(string tutorUserId)
        {
            // Lấy tất cả feedbacks có rating (bỏ điều kiện class status để đếm chính xác)
            var allFeedbacks = await _context.Feedbacks
                .Where(f => f.ToUserId == tutorUserId && f.Rating != null)
                .ToListAsync();

            if (allFeedbacks.Count == 0) return (0, 0);

            // Group by user để tính avg per rater
            var perUser = allFeedbacks
                .GroupBy(f => f.FromUserId)
                .Select(g => new
                {
                    FromUserId = g.Key!,
                    UserAvg = g.Average(x => (double)x.Rating!)
                })
                .ToList();

            var overall = perUser.Average(x => x.UserAvg);
            
            // Return (rating average, TOTAL feedback count)
            return (overall, allFeedbacks.Count);
        }
    }
}
