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
    public class ReportRepository : GenericRepository<Report>, IReportRepository
    {
        public ReportRepository(TpeduContext ctx) : base(ctx) { }

        public async Task<(IReadOnlyList<Report> items, int total)> GetPagedForTutorAsync(string tutorUserId, ReportStatus? status, string? keyword, int page, int pageSize)
        {
            var q = _dbSet.AsNoTracking()
                .Include(r => r.Reporter)
                .Where(r => r.TargetUserId == tutorUserId && r.DeletedAt == null);

            if (status.HasValue) q = q.Where(r => r.Status == status);
            if (!string.IsNullOrWhiteSpace(keyword)) q = q.Where(r => r.Description!.Contains(keyword));

            var total = await q.CountAsync();
            var items = await q.OrderByDescending(r => r.CreatedAt)
                               .Skip((page - 1) * pageSize)
                               .Take(pageSize)
                               .ToListAsync();
            return (items, total);
        }

        public async Task<(IReadOnlyList<Report> items, int total)> GetPagedForAdminAsync(ReportStatus? status, string? keyword, int page, int pageSize)
        {
            // Admin sees ALL reports (material, user, lesson)
            var q = _dbSet.AsNoTracking()
                .Include(r => r.Reporter)
                .Include(r => r.TargetUser)
                .Where(r => r.DeletedAt == null);

            if (status.HasValue) q = q.Where(r => r.Status == status);
            if (!string.IsNullOrWhiteSpace(keyword)) q = q.Where(r => r.Description!.Contains(keyword));

            var total = await q.CountAsync();
            var items = await q.OrderByDescending(r => r.CreatedAt)
                               .Skip((page - 1) * pageSize)
                               .Take(pageSize)
                               .ToListAsync();
            return (items, total);
        }

        public async Task<Report?> GetDetailAsync(string id)
        {
            return await _dbSet
                .Where(r => r.DeletedAt == null)
                .Include(r => r.Reporter)
                .Include(r => r.TargetUser)
                .Include(r => r.TargetLesson)
                .Include(r => r.TargetMedia)
                .FirstOrDefaultAsync(r => r.Id == id);
        }

        public async Task<bool> HasRecentReportAsync(string studentUserId, string mediaId, DateTime since)
        {
            return await _dbSet.AsNoTracking()
                .AnyAsync(r => r.ReporterId == studentUserId 
                            && r.TargetMediaId == mediaId 
                            && r.CreatedAt > since 
                            && r.DeletedAt == null);
        }

        public async Task<int> CountDistinctMaterialsReportedAsync(string studentUserId, DateTime since)
        {
            return await _dbSet.AsNoTracking()
                .Where(r => r.ReporterId == studentUserId 
                         && r.CreatedAt > since 
                         && r.DeletedAt == null)
                .Select(r => r.TargetMediaId)
                .Distinct()
                .CountAsync();
        }

        public async Task<bool> HasRecentAutoReportAsync(string studentId, string classId, DateTime since)
        {
            return await _dbSet.AsNoTracking()
                .AnyAsync(r => r.ReporterId == studentId
                            && r.Description != null
                            && r.Description.Contains("[AUTO-REPORT]")
                            && r.Description.Contains($"[ClassId:{classId}]")
                            && r.CreatedAt > since
                            && r.DeletedAt == null);
        }

        public async Task<(IReadOnlyList<Report> items, int total)> GetAutoReportsPagedAsync(
            string? classId,
            string? studentId,
            DateTime? fromDate,
            DateTime? toDate,
            bool? hasResponse,
            string sortBy,
            bool sortDescending,
            int page,
            int pageSize)
        {
            // Base filter: only auto-reports
            var query = _dbSet.AsNoTracking()
                .Where(r => r.DeletedAt == null
                         && r.Description != null
                         && r.Description.Contains("[AUTO-REPORT]"));

            // Apply filters
            if (!string.IsNullOrEmpty(classId))
            {
                query = query.Where(r => r.Description!.Contains($"[ClassId:{classId}]"));
            }

            if (!string.IsNullOrEmpty(studentId))
            {
                query = query.Where(r => r.ReporterId == studentId);
            }

            if (fromDate.HasValue)
            {
                query = query.Where(r => r.CreatedAt >= fromDate.Value);
            }

            if (toDate.HasValue)
            {
                var endOfDay = toDate.Value.AddDays(1);
                query = query.Where(r => r.CreatedAt < endOfDay);
            }

            if (hasResponse.HasValue)
            {
                query = hasResponse.Value
                    ? query.Where(r => r.StudentResponse != null)
                    : query.Where(r => r.StudentResponse == null);
            }

            // Get total count before pagination
            var total = await query.CountAsync();

            // Apply sorting
            query = sortBy.ToLower() switch
            {
                "respondedat" => sortDescending
                    ? query.OrderByDescending(r => r.StudentRespondedAt)
                    : query.OrderBy(r => r.StudentRespondedAt),
                _ => sortDescending
                    ? query.OrderByDescending(r => r.CreatedAt)
                    : query.OrderBy(r => r.CreatedAt)
            };

            // Apply pagination
            var items = await query
                .Skip((page - 1) * pageSize)
                .Take(pageSize)
                .ToListAsync();

            return (items, total);
        }
    }
}
