using BusinessLayer.DTOs.Tutor;
using BusinessLayer.Helper;
using BusinessLayer.Service.Interface;
using DataLayer.Enum;
using DataLayer.Repositories.Abstraction;
using Microsoft.EntityFrameworkCore;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace BusinessLayer.Service
{
    /// <summary>
    /// Service lấy thống kê cho Tutor Dashboard
    /// </summary>
    public class TutorDashboardService : ITutorDashboardService
    {
        private readonly IUnitOfWork _uow;

        public TutorDashboardService(IUnitOfWork uow)
        {
            _uow = uow;
        }

        public async Task<TutorDashboardDto> GetDashboardStatisticsAsync(string tutorUserId)
        {
            // Lấy TutorProfile từ UserId
            var tutorProfile = await _uow.TutorProfiles.GetByUserIdAsync(tutorUserId);
            if (tutorProfile == null)
                throw new InvalidOperationException("Không tìm thấy thông tin gia sư");

            var tutorProfileId = tutorProfile.Id;
            var now = DateTimeHelper.VietnamNow;
            var startOfMonth = new DateTime(now.Year, now.Month, 1);
            var startOfWeek = now.AddDays(-(int)now.DayOfWeek); // Sunday

            // ===== Part 1: Basic Statistics =====
            
            // Số lớp đang dạy (Status = Ongoing)
            var activeClasses = await _uow.Classes.GetAllAsync(
                c => c.TutorId == tutorProfileId && c.Status == ClassStatus.Ongoing);
            var totalActiveClasses = activeClasses.Count();

            // Tổng số học viên trong các lớp đang dạy
            var totalStudents = activeClasses.Sum(c => c.CurrentStudentCount);

            // Tổng thu nhập tháng này (từ Escrow đã release)
            var monthlyIncome = await GetMonthlyIncomeAsync(tutorUserId, startOfMonth);

            // ===== Part 2: Lesson Statistics =====
            
            // Số buổi dạy trong tuần này
            var lessonsThisWeek = await GetLessonCountAsync(tutorProfileId, startOfWeek, now);
            
            // Số buổi dạy trong tháng này
            var lessonsThisMonth = await GetLessonCountAsync(tutorProfileId, startOfMonth, now);

            // ===== Part 3: Student Statistics =====
            
            // Học viên đang học hiện tại (trong các lớp Ongoing)
            var activeStudents = totalStudents; // Cùng với totalStudents

            // Học viên mới trong tháng (ClassAssign được approved trong tháng)
            var newStudentsThisMonth = await GetNewStudentsThisMonthAsync(tutorProfileId, startOfMonth);

            return new TutorDashboardDto
            {
                TotalActiveClasses = totalActiveClasses,
                TotalStudents = totalStudents,
                MonthlyIncome = monthlyIncome,
                LessonsThisWeek = lessonsThisWeek,
                LessonsThisMonth = lessonsThisMonth,
                ActiveStudents = activeStudents,
                NewStudentsThisMonth = newStudentsThisMonth
            };
        }

        /// <summary>
        /// Lấy thống kê thu nhập theo từng tháng trong năm
        /// </summary>
        public async Task<YearlyIncomeDto> GetYearlyIncomeAsync(string tutorUserId, int year)
        {
            var wallet = await _uow.Wallets.GetByUserIdAsync(tutorUserId);
            
            var result = new YearlyIncomeDto
            {
                Year = year,
                MonthlyData = new List<MonthlyStatDto>()
            };

            if (wallet == null)
            {
                // Trả về 12 tháng với amount = 0
                for (int m = 1; m <= 12; m++)
                    result.MonthlyData.Add(new MonthlyStatDto { Month = m, Amount = 0 });
                return result;
            }

            // Lấy tất cả transaction PayoutIn trong năm
            var startOfYear = new DateTime(year, 1, 1);
            var endOfYear = new DateTime(year, 12, 31, 23, 59, 59);

            var transactions = await _uow.Transactions.GetAllAsync(
                t => t.WalletId == wallet.Id 
                    && t.Type == TransactionType.PayoutIn
                    && t.Status == TransactionStatus.Succeeded
                    && t.CreatedAt >= startOfYear
                    && t.CreatedAt <= endOfYear);

            // Group theo tháng
            var monthlyGroups = transactions
                .GroupBy(t => t.CreatedAt.Month)
                .ToDictionary(g => g.Key, g => g.Sum(t => t.Amount));

            // Tạo dữ liệu 12 tháng
            for (int m = 1; m <= 12; m++)
            {
                result.MonthlyData.Add(new MonthlyStatDto
                {
                    Month = m,
                    Amount = monthlyGroups.ContainsKey(m) ? monthlyGroups[m] : 0
                });
            }

            result.TotalIncome = result.MonthlyData.Sum(x => x.Amount);
            return result;
        }

        /// <summary>
        /// Lấy thống kê buổi học theo từng tháng trong năm
        /// </summary>
        public async Task<YearlyLessonsDto> GetYearlyLessonsAsync(string tutorUserId, int year)
        {
            var tutorProfile = await _uow.TutorProfiles.GetByUserIdAsync(tutorUserId);
            
            var result = new YearlyLessonsDto
            {
                Year = year,
                MonthlyData = new List<MonthlyLessonStatDto>()
            };

            if (tutorProfile == null)
            {
                // Trả về 12 tháng với count = 0
                for (int m = 1; m <= 12; m++)
                    result.MonthlyData.Add(new MonthlyLessonStatDto { Month = m, LessonCount = 0 });
                return result;
            }

            var tutorProfileId = tutorProfile.Id;

            // Lấy các lớp của tutor
            var classes = await _uow.Classes.GetAllAsync(c => c.TutorId == tutorProfileId);
            var classIds = classes.Select(c => c.Id).ToList();

            if (!classIds.Any())
            {
                for (int m = 1; m <= 12; m++)
                    result.MonthlyData.Add(new MonthlyLessonStatDto { Month = m, LessonCount = 0 });
                return result;
            }

            // Lấy tất cả lesson trong năm với ScheduleEntry để có StartTime thực sự
            var startOfYear = new DateTime(year, 1, 1);
            var endOfYear = new DateTime(year, 12, 31, 23, 59, 59);

            // Lấy lessons với ScheduleEntries để có ngày thực sự diễn ra
            var lessons = await _uow.Lessons.GetAllAsync(
                l => classIds.Contains(l.ClassId!) 
                    && l.Status != LessonStatus.CANCELLED,
                includes: q => q.Include(l => l.ScheduleEntries));

            // Lọc theo ScheduleEntry.StartTime trong năm và group theo tháng
            var monthlyGroups = lessons
                .SelectMany(l => l.ScheduleEntries
                    .Where(se => se.DeletedAt == null 
                        && se.StartTime >= startOfYear 
                        && se.StartTime <= endOfYear))
                .GroupBy(se => se.StartTime.Month)
                .ToDictionary(g => g.Key, g => g.Count());

            // Tạo dữ liệu 12 tháng
            for (int m = 1; m <= 12; m++)
            {
                result.MonthlyData.Add(new MonthlyLessonStatDto
                {
                    Month = m,
                    LessonCount = monthlyGroups.ContainsKey(m) ? monthlyGroups[m] : 0
                });
            }

            result.TotalLessons = result.MonthlyData.Sum(x => x.LessonCount);
            return result;
        }

        private async Task<decimal> GetMonthlyIncomeAsync(string tutorUserId, DateTime startOfMonth)
        {
            // Lấy wallet của tutor
            var wallet = await _uow.Wallets.GetByUserIdAsync(tutorUserId);
            if (wallet == null) return 0;

            // Lấy các transaction PayoutIn trong tháng (tiền tutor nhận được)
            var transactions = await _uow.Transactions.GetAllAsync(
                t => t.WalletId == wallet.Id 
                    && t.Type == TransactionType.PayoutIn
                    && t.Status == TransactionStatus.Succeeded
                    && t.CreatedAt >= startOfMonth);

            return transactions.Sum(t => t.Amount);
        }

        private async Task<int> GetLessonCountAsync(string tutorProfileId, DateTime from, DateTime to)
        {
            // Lấy các lớp của tutor
            var classes = await _uow.Classes.GetAllAsync(c => c.TutorId == tutorProfileId);
            var classIds = classes.Select(c => c.Id).ToList();

            if (!classIds.Any()) return 0;

            // Đếm số lesson trong khoảng thời gian dựa trên ScheduleEntry.StartTime
            var lessons = await _uow.Lessons.GetAllAsync(
                l => classIds.Contains(l.ClassId!) 
                    && l.Status != LessonStatus.CANCELLED,
                includes: q => q.Include(l => l.ScheduleEntries));

            // Đếm các schedule entries nằm trong khoảng thời gian
            return lessons
                .SelectMany(l => l.ScheduleEntries)
                .Count(se => se.DeletedAt == null 
                    && se.StartTime >= from 
                    && se.StartTime <= to);
        }

        private async Task<int> GetNewStudentsThisMonthAsync(string tutorProfileId, DateTime startOfMonth)
        {
            // Lấy các lớp của tutor
            var classes = await _uow.Classes.GetAllAsync(c => c.TutorId == tutorProfileId);
            var classIds = classes.Select(c => c.Id).ToList();

            if (!classIds.Any()) return 0;

            // Đếm ClassAssign được tạo trong tháng
            var classAssigns = await _uow.ClassAssigns.GetAllAsync(
                ca => classIds.Contains(ca.ClassId!) 
                    && ca.CreatedAt >= startOfMonth
                    && ca.ApprovalStatus == ApprovalStatus.Approved);

            // Đếm số unique student
            return classAssigns.Select(ca => ca.StudentId).Distinct().Count();
        }
    }
}

