using BusinessLayer.DTOs.Admin.Directory;
using BusinessLayer.DTOs.Media;
using BusinessLayer.Helper;
using BusinessLayer.Service.Interface;
using DataLayer.Entities;
using DataLayer.Enum;
using DataLayer.Repositories.Abstraction;
using Microsoft.EntityFrameworkCore;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using static BusinessLayer.DTOs.Admin.Directory.AdminListDtos;

namespace BusinessLayer.Service
{
    public class AdminDirectoryService : IAdminDirectoryService
    {
        private readonly IUnitOfWork _uow;
        public AdminDirectoryService(IUnitOfWork uow) => _uow = uow;

        public async Task<PaginationResult<TutorListItemDto>> GetTutorsPagedAsync(int page, int pageSize)
        {
            var result = await _uow.Users.GetPagedByRoleAsync("Tutor", page, pageSize);

            var mapped = result.Data.Select(u => new TutorListItemDto
            {
                TutorId = u.Id,
                Username = u.UserName,
                Email = u.Email!,
                Status = u.Status.ToString(),
                IsBanned = u.IsBanned || u.Status == AccountStatus.Banned,
                CreateDate = u.CreatedAt
            }).ToList();

            return new PaginationResult<TutorListItemDto>(mapped, result.TotalCount, result.PageNumber, result.PageSize);
        }

        public async Task<AdminStudentListPageDto> GetStudentsPagedAsync(int page, int pageSize)
        {
            var result = await _uow.Users.GetPagedByRoleAsync("Student", page, pageSize);
            return new AdminStudentListPageDto
            {
                Data = result.Data.Select(u => new StudentListItemDto
                {
                    StudentId = u.Id,
                    Username = u.UserName,
                    Email = u.Email!,
                    IsBanned = u.IsBanned || u.Status == AccountStatus.Banned,
                    CreateDate = u.CreatedAt
                }).ToList(),
                TotalCount = result.TotalCount,
                Page = result.PageNumber,
                PageSize = result.PageSize
            };
        }

        public async Task<AdminParentListPageDto> GetParentsPagedAsync(int page, int pageSize)
        {
            var result = await _uow.Users.GetPagedByRoleAsync("Parent", page, pageSize);
            return new AdminParentListPageDto
            {
                Data = result.Data.Select(u => new ParentListItemDto
                {
                    ParentId = u.Id,
                    Username = u.UserName,
                    Email = u.Email!,
                    IsBanned = u.IsBanned || u.Status == AccountStatus.Banned,
                    CreateDate = u.CreatedAt
                }).ToList(),
                TotalCount = result.TotalCount,
                Page = result.PageNumber,
                PageSize = result.PageSize
            };
        }

        public async Task<AdminStudentDetailDto?> GetStudentDetailAsync(string userId)
        {
            var u = await _uow.Users.GetDetailByIdAsync(userId);
            if (u == null || u.RoleName != "Student") return null;

            var sp = await _uow.StudentProfiles.GetByUserIdAsync(userId);

            // Lấy tên bậc học (ưu tiên navigation)
            string? eduName = null;
            //if (sp?.EducationLevelId != null)
            //{
            //    eduName = sp.EducationLevel?.LevelName;
            //    if (eduName == null)
            //    {
            //        var edu = await _uow.EducationLevels.GetByIdAsync(sp.EducationLevelId);
            //        eduName = edu?.LevelName;
            //    }
            //}

            // Identity documents (nếu có)
            var idDocs = await _uow.Media.GetByOwnerAndContextAsync(userId, UploadContext.IdentityDocument);
            var idDocDtos = idDocs.Select(m => new MediaItemDto
            {
                Id = m.Id,
                Url = m.FileUrl,
                FileName = m.FileName,
                ContentType = m.MediaType,
                FileSize = m.FileSize
            }).ToList();

            return new AdminStudentDetailDto
            {
                StudentId = u.Id,
                Username = u.UserName,
                Email = u.Email!,
                AvatarUrl = u.AvatarUrl,
                Phone = u.Phone,
                Address = u.Address,
                Gender = u.Gender?.ToString().ToLowerInvariant(),
                DateOfBirth = u.DateOfBirth,

                Status = u.Status.ToString(),
                IsBanned = u.IsBanned || u.Status == AccountStatus.Banned,
                CreateDate = u.CreatedAt,
                UpdatedAt = u.UpdatedAt,
                BannedAt = u.BannedAt,
                BannedUntil = u.BannedUntil,
                BannedReason = u.BannedReason,

                //EducationLevelId = sp?.EducationLevelId,
                EducationLevelName = eduName,
                PreferredSubjects = sp?.PreferredSubjects,

                IdentityDocuments = idDocDtos
            };
        }

        public async Task<AdminParentDetailDto?> GetParentDetailAsync(string userId)
        {
            var u = await _uow.Users.GetDetailByIdAsync(userId);
            if (u == null || u.RoleName != "Parent") return null;

            // Lấy tất cả con của parent
            var children = await _uow.ParentProfiles.GetChildrenAllAsync(userId);

            // Identity documents (nếu có)
            var idDocs = await _uow.Media.GetByOwnerAndContextAsync(userId, UploadContext.IdentityDocument);
            var idDocDtos = idDocs.Select(m => new MediaItemDto
            {
                Id = m.Id,
                Url = m.FileUrl,
                FileName = m.FileName,
                ContentType = m.MediaType,
                FileSize = m.FileSize
            }).ToList();

            var dto = new AdminParentDetailDto
            {
                ParentId = u.Id,
                Username = u.UserName,
                Email = u.Email!,
                AvatarUrl = u.AvatarUrl,
                Phone = u.Phone,
                Address = u.Address,
                Gender = u.Gender?.ToString().ToLowerInvariant(),
                DateOfBirth = u.DateOfBirth,

                Status = u.Status.ToString(),
                IsBanned = u.IsBanned || u.Status == AccountStatus.Banned,
                CreateDate = u.CreatedAt,
                UpdatedAt = u.UpdatedAt,
                BannedAt = u.BannedAt,
                BannedUntil = u.BannedUntil,
                BannedReason = u.BannedReason,

                // ✅ map mảng Children
                Children = children.Select(x => new AdminChildBriefDto
                {
                    StudentId = x.stu.Id,
                    StudentUserId = x.childUser.Id,
                    Username = x.childUser.UserName,
                    Email = x.childUser.Email!,
                    AvatarUrl = x.childUser.AvatarUrl,
                    CreateDate = x.stu.CreatedAt,
                    Relationship = x.link.Relationship,
                    EducationLevel = x.stu.EducationLevel      // chuỗi
                }).ToList(),

                IdentityDocuments = idDocDtos
            };

            return dto;
        }

        public async Task<object> GetDashboardStatisticsAsync()
        {
            var now = DateTimeHelper.VietnamNow;
            var thisMonthStart = new DateTime(now.Year, now.Month, 1);
            var lastMonthStart = thisMonthStart.AddMonths(-1);

            // Count users by role - using GetAllAsync and count in memory
            var allUsers = await _uow.Users.GetAllAsync();
            var studentsCount = allUsers.Count(u => u.RoleName == "Student");
            var parentsCount = allUsers.Count(u => u.RoleName == "Parent");
            var tutorsCount = allUsers.Count(u => u.RoleName == "Tutor");
            var totalUsersCount = studentsCount + parentsCount + tutorsCount;

            // Calculate user growth
            var usersThisMonth = allUsers.Count(u => u.CreatedAt >= thisMonthStart);
            var usersLastMonth = allUsers.Count(u => u.CreatedAt >= lastMonthStart && u.CreatedAt < thisMonthStart);
            var userGrowth = usersLastMonth > 0 
                ? ((double)(usersThisMonth - usersLastMonth) / usersLastMonth) * 100 
                : (usersThisMonth > 0 ? 100 : 0);

            // Count classes by status
            var allClasses = await _uow.Classes.GetAllAsync();
            var allClassesList = allClasses.ToList();
            var totalClassesCount = allClassesList.Count;
            var ongoingClasses = allClassesList.Count(c => c.Status == ClassStatus.Ongoing);
            var completedClasses = allClassesList.Count(c => c.Status == ClassStatus.Completed);
            var pendingClasses = allClassesList.Count(c => c.Status == ClassStatus.Pending);

            // Calculate class growth
            var classesThisMonth = allClassesList.Count(c => c.CreatedAt >= thisMonthStart);
            var classesLastMonth = allClassesList.Count(c => c.CreatedAt >= lastMonthStart && c.CreatedAt < thisMonthStart);
            var classGrowth = classesLastMonth > 0 
                ? ((double)(classesThisMonth - classesLastMonth) / classesLastMonth) * 100 
                : (classesThisMonth > 0 ? 100 : 0);

            // Count transactions
            var allTransactions = await _uow.Transactions.GetAllAsync();
            var totalTransactionsCount = allTransactions.Count();
            var successTransactions = allTransactions.Count(t => t.Status == TransactionStatus.Succeeded);
            var pendingTransactions = allTransactions.Count(t => t.Status == TransactionStatus.Pending);
            var failedTransactions = allTransactions.Count(t => t.Status == TransactionStatus.Failed);

            // Calculate transaction growth
            var transactionsThisMonth = allTransactions.Count(t => t.CreatedAt >= thisMonthStart);
            var transactionsLastMonth = allTransactions.Count(t => t.CreatedAt >= lastMonthStart && t.CreatedAt < thisMonthStart);
            var transactionGrowth = transactionsLastMonth > 0 
                ? ((double)(transactionsThisMonth - transactionsLastMonth) / transactionsLastMonth) * 100 
                : (transactionsThisMonth > 0 ? 100 : 0);

            // DOANH THU HỆ THỐNG = Tổng từ 3 nguồn:
            // 1. Hoa hồng (Commission): Khi học sinh thanh toán học phí và hoàn thành buổi học
            //    - TransactionType.Commission
            //    - Được tạo khi release escrow (EscrowService.ReleaseEscrowAsync)
            // 2. Phí tạo lớp từ gia sư: Khi gia sư tạo lớp mới
            //    - TransactionType.PayoutIn với note chứa "Phí tạo lớp"
            //    - Được tạo khi tutor tạo lớp (ClassService.CreateRecurringClassScheduleAsync)
            // 3. Phí tạo request từ học sinh: Khi học sinh tạo yêu cầu tìm gia sư
            //    - TransactionType.PayoutIn với note chứa "Phí tạo yêu cầu"
            //    - Được tạo khi student tạo request (ClassRequestService.CreateClassRequestAsync)
            
            // Tính tổng doanh thu từ tất cả transactions thành công
            var totalRevenue = allTransactions
                .Where(t => t.Status == TransactionStatus.Succeeded 
                    && (t.Type == TransactionType.Commission || 
                        (t.Type == TransactionType.PayoutIn && 
                         t.Note != null && (t.Note.Contains("Phí tạo lớp") || t.Note.Contains("Phí tạo yêu cầu")))))
                .Sum(t => (decimal?)t.Amount) ?? 0;
            var revenueThisMonth = allTransactions
                .Where(t => t.CreatedAt >= thisMonthStart 
                    && t.Status == TransactionStatus.Succeeded 
                    && (t.Type == TransactionType.Commission || 
                        (t.Type == TransactionType.PayoutIn && 
                         (t.Note != null && (t.Note.Contains("Phí tạo lớp") || t.Note.Contains("Phí tạo yêu cầu"))))))
                .Sum(t => (decimal?)t.Amount) ?? 0;

            var revenueLastMonth = allTransactions
                .Where(t => t.CreatedAt >= lastMonthStart 
                    && t.CreatedAt < thisMonthStart 
                    && t.Status == TransactionStatus.Succeeded 
                    && (t.Type == TransactionType.Commission || 
                        (t.Type == TransactionType.PayoutIn && 
                         (t.Note != null && (t.Note.Contains("Phí tạo lớp") || t.Note.Contains("Phí tạo yêu cầu"))))))
                .Sum(t => (decimal?)t.Amount) ?? 0;

            // Chi tiết doanh thu theo nguồn (tháng này)
            // 1. Hoa hồng: Commission từ các lớp học đã hoàn thành
            var commissionThisMonth = allTransactions
                .Where(t => t.CreatedAt >= thisMonthStart 
                    && t.Status == TransactionStatus.Succeeded 
                    && t.Type == TransactionType.Commission)
                .Sum(t => (decimal?)t.Amount) ?? 0;

            // 2. Phí tạo lớp: Từ gia sư khi tạo lớp mới (50,000 VND/lớp)
            var classCreationFeeThisMonth = allTransactions
                .Where(t => t.CreatedAt >= thisMonthStart 
                    && t.Status == TransactionStatus.Succeeded 
                    && t.Type == TransactionType.PayoutIn
                    && t.Note != null 
                    && t.Note.Contains("Phí tạo lớp"))
                .Sum(t => (decimal?)t.Amount) ?? 0;

            // 3. Phí tạo request: Từ học sinh khi tạo yêu cầu tìm gia sư (30,000 VND/request)
            var requestCreationFeeThisMonth = allTransactions
                .Where(t => t.CreatedAt >= thisMonthStart 
                    && t.Status == TransactionStatus.Succeeded 
                    && t.Type == TransactionType.PayoutIn
                    && t.Note != null 
                    && t.Note.Contains("Phí tạo yêu cầu"))
                .Sum(t => (decimal?)t.Amount) ?? 0;

            var revenueGrowth = revenueLastMonth > 0 
                ? ((double)(revenueThisMonth - revenueLastMonth) / (double)revenueLastMonth) * 100 
                : (revenueThisMonth > 0 ? 100 : 0);

            return new
            {
                totalUsers = new
                {
                    total = totalUsersCount,
                    students = studentsCount,
                    parents = parentsCount,
                    tutors = tutorsCount,
                    growth = Math.Round(userGrowth, 1)
                },
                totalClasses = new
                {
                    total = totalClassesCount,
                    ongoing = ongoingClasses,
                    completed = completedClasses,
                    pending = pendingClasses,
                    growth = Math.Round(classGrowth, 1)
                },
                totalTransactions = new
                {
                    total = totalTransactionsCount,
                    success = successTransactions,
                    pending = pendingTransactions,
                    failed = failedTransactions,
                    growth = Math.Round(transactionGrowth, 1)
                },
                revenue = new
                {
                    total = totalRevenue,
                    thisMonth = revenueThisMonth,
                    lastMonth = revenueLastMonth,
                    growth = Math.Round(revenueGrowth, 1),
                    breakdown = new
                    {
                        commission = commissionThisMonth,
                        classCreationFee = classCreationFeeThisMonth,
                        requestCreationFee = requestCreationFeeThisMonth
                    }
                }
            };
        }
    }
}
