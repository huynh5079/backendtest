using BusinessLayer.Helper;
using BusinessLayer.DTOs.API;
using BusinessLayer.Reports;
using BusinessLayer.Service.Interface;
using DataLayer.Entities;
using DataLayer.Enum;
using DataLayer.Repositories.Abstraction;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace BusinessLayer.Service
{
    public class ReportService : IReportService
    {
        private readonly IUnitOfWork _uow;
        private readonly INotificationService _notificationService;
        private readonly ITokenService _tokenService;

        public ReportService(
            IUnitOfWork uow, 
            INotificationService notificationService,
            ITokenService tokenService)
        {
            _uow = uow;
            _notificationService = notificationService;
            _tokenService = tokenService;
        }

        // map helpers
        private static ReportItemDto MapItem(Report r) => new()
        {
            Id = r.Id,
            ReporterId = r.ReporterId,
            ReporterEmail = r.Reporter?.Email,
            ReporterUsername = r.Reporter?.UserName,
            TargetUserId = r.TargetUserId,
            TargetLessonId = r.TargetLessonId,
            TargetMediaId = r.TargetMediaId,
            Description = r.Description,
            Status = r.Status,
            CreatedAt = r.CreatedAt
        };

        private static ReportDetailDto MapDetail(Report r) => new()
        {
            Id = r.Id,
            ReporterId = r.ReporterId,
            TargetUserId = r.TargetUserId,
            TargetLessonId = r.TargetLessonId,
            TargetMediaId = r.TargetMediaId,
            Description = r.Description,
            Status = r.Status,
            CreatedAt = r.CreatedAt,
            ReporterEmail = r.Reporter?.Email,
            ReporterUsername = r.Reporter?.UserName,
            TargetUserEmail = r.TargetUser?.Email,
            TargetUsername = r.TargetUser?.UserName,
            LessonTitle = r.TargetLesson?.Title,
            MediaFileName = r.TargetMedia?.FileName
        };

        public async Task<string> CreateToTutorAsync(string studentUserId, string lessonId, string mediaId, string reason)
            => await CreateCoreAsync(studentUserId, lessonId, mediaId, reason, routeToAdmin: false);

        public async Task<string> CreateToAdminAsync(string studentUserId, string lessonId, string mediaId, string reason)
            => await CreateCoreAsync(studentUserId, lessonId, mediaId, reason, routeToAdmin: true);

        private async Task<string> CreateCoreAsync(string studentUserId, string lessonId, string mediaId, string reason, bool routeToAdmin)
        {
            // Check rate limiting và duplicate trước khi validate lesson
            await CheckRateLimitAsync(studentUserId);
            await CheckDuplicateReportAsync(studentUserId, mediaId);

            // Validate lesson, class, enrollment (để ở repo qua uow)
            var (lesson, cls) = await _uow.Lessons.GetWithClassAsync(lessonId);

            var studentProfileId = await _uow.StudentProfiles.GetIdByUserIdAsync(studentUserId)
                ?? throw new UnauthorizedAccessException("Tài khoản học sinh không hợp lệ.");

            var isEnrolled = await _uow.ClassAssigns.IsApprovedAsync(cls.Id, studentProfileId);
            if (!isEnrolled) throw new UnauthorizedAccessException("Bạn không thuộc lớp này.");

            var media = await _uow.Media.GetAsync(m => m.Id == mediaId &&
                                                       m.LessonId == lessonId &&
                                                       m.Context == UploadContext.Material &&
                                                       m.DeletedAt == null)
                       ?? throw new KeyNotFoundException("Không tìm thấy tài liệu để báo cáo.");

            var tutorUserId = await _uow.TutorProfiles.GetTutorUserIdByTutorProfileIdAsync(cls.TutorId);

            var report = new Report
            {
                Id = Guid.NewGuid().ToString(),
                ReporterId = studentUserId,
                TargetUserId = routeToAdmin ? null : tutorUserId,
                TargetLessonId = lessonId,
                TargetMediaId = media.Id,
                Description = $"[Material:{media.Id}] {reason}",
                Status = ReportStatus.Pending,
                CreatedAt = DateTimeHelper.GetVietnamTime()
            };

            await _uow.Reports.CreateAsync(report);
            await _uow.SaveChangesAsync();

            // Gửi notification cho Tutor hoặc Admin
            try
            {
                var targetUserId = routeToAdmin ? null : tutorUserId;
                var targetName = routeToAdmin ? "Admin" : "Tutor";
                var reporterEmail = (await _uow.Users.GetAsync(u => u.Id == studentUserId))?.Email ?? "Học sinh";
                
                if (targetUserId != null)
                {
                    // Notify Tutor
                    await _notificationService.CreateSystemAnnouncementNotificationAsync(
                        targetUserId,
                        "Có report mới về tài liệu",
                        $"Học sinh {reporterEmail} đã report tài liệu: {media.FileName}",
                        report.Id
                    );
                    await _notificationService.SendRealTimeNotificationAsync(
                        targetUserId, 
                        await _uow.Notifications.GetAsync(n => n.Id == report.Id) ?? new Notification()
                    );
                }
                // TODO: Notify Admin when routeToAdmin = true (need Admin's userId)
            }
            catch (Exception)
            {
                // Log but don't fail the report creation if notification fails
            }

            return report.Id;
        }

        public async Task<(IReadOnlyList<ReportItemDto> items, int total)> GetForTutorAsync(string tutorUserId, ReportQuery q)
        {
            var (items, total) = await _uow.Reports.GetPagedForTutorAsync(
                tutorUserId, q.Status, q.Keyword, q.Page, q.PageSize);
            return (items.Select(MapItem).ToList(), total);
        }

        public async Task<(IReadOnlyList<ReportItemDto> items, int total)> GetForAdminAsync(ReportQuery q)
        {
            var (items, total) = await _uow.Reports.GetPagedForAdminAsync(
                q.Status, q.Keyword, q.Page, q.PageSize);
            return (items.Select(MapItem).ToList(), total);
        }

        public async Task<ReportDetailDto> GetDetailAsync(string actorUserId, string id, bool isAdmin)
        {
            var r = await _uow.Reports.GetDetailAsync(id) ?? throw new KeyNotFoundException("Không tìm thấy report.");
            if (!isAdmin && r.TargetUserId != actorUserId)
                throw new UnauthorizedAccessException("Bạn không có quyền xem report này.");
            return MapDetail(r);
        }

        public async Task<bool> UpdateStatusAsync(string actorUserId, string id, ReportStatus status, bool isAdmin)
        {
            var r = await _uow.Reports.GetAsync(x => x.Id == id) ?? throw new KeyNotFoundException("Không tìm thấy report.");
            if (!isAdmin && r.TargetUserId != actorUserId)
                throw new UnauthorizedAccessException("Bạn không có quyền cập nhật report này.");

            r.Status = status;
            await _uow.Reports.UpdateAsync(r);
            await _uow.SaveChangesAsync();

            // Notify Reporter (Student) about status change
            try
            {
                if (r.ReporterId != null)
                {
                    var statusText = status switch
                    {
                        ReportStatus.InReview => "đang được xem xét",
                        ReportStatus.Resolved => "đã được xử lý",
                        ReportStatus.Rejected => "bị từ chối",
                        ReportStatus.Escalated => "được chuyển cấp cao hơn",
                        _ => "được cập nhật"
                    };

                    var notification = await _notificationService.CreateSystemAnnouncementNotificationAsync(
                        r.ReporterId,
                        "Cập nhật trạng thái report",
                        $"Report của bạn {statusText}. Trạng thái: {status}",
                        id
                    );
                    await _uow.SaveChangesAsync();
                    await _notificationService.SendRealTimeNotificationAsync(r.ReporterId, notification);
                }
            }
            catch (Exception)
            {
                // Log but don't fail the status update if notification fails
            }

            return true;
        }

        public async Task<bool> CancelReportAsync(string actorUserId, string id, bool isAdmin)
        {
            var r = await _uow.Reports.GetAsync(x => x.Id == id && x.DeletedAt == null) 
                ?? throw new KeyNotFoundException("Không tìm thấy report.");
            
            if (!isAdmin && r.TargetUserId != actorUserId)
                throw new UnauthorizedAccessException("Bạn không có quyền hủy report này.");

            r.DeletedAt = DateTimeHelper.GetVietnamTime();
            await _uow.Reports.UpdateAsync(r);
            await _uow.SaveChangesAsync();
            return true;
        }

        public async Task<string> ReportUserAsync(string reporterUserId, string targetUserId, string reason)
        {
            // 1. Validate không tự report bản thân
            if (reporterUserId == targetUserId)
                throw new InvalidOperationException("Bạn không thể báo cáo chính mình.");

            // 2. Check rate limit (max 5 user reports/day)
            var since = DateTimeHelper.GetVietnamTime().AddDays(-1);
            var userReportCount = await _uow.Reports.GetAllAsync(
                filter: r => r.ReporterId == reporterUserId 
                    && r.TargetUserId != null 
                    && r.CreatedAt >= since 
                    && r.DeletedAt == null
            );
            if (userReportCount.Count() >= 5)
                throw new InvalidOperationException("Bạn đã đạt giới hạn 5 báo cáo người dùng mỗi ngày.");

            // 3. Validate target user exists
            var targetUser = await _uow.Users.GetByIdAsync(targetUserId)
                ?? throw new KeyNotFoundException("Không tìm thấy người dùng được báo cáo.");

            // 4. Validate reporter và target cùng ít nhất 1 lớp
            var reporter = await _uow.Users.GetByIdAsync(reporterUserId)
                ?? throw new KeyNotFoundException("Không tìm thấy thông tin người báo cáo.");

            bool hasSharedClass = false;

            // Check if both are in same class (student-student or student-tutor)
            if (reporter.RoleName == "Student")
            {
                var reporterStudentId = await _uow.StudentProfiles.GetIdByUserIdAsync(reporterUserId);
                if (reporterStudentId != null)
                {
                    // Get all class IDs where reporter is approved
                    var reporterClassAssigns = await _uow.ClassAssigns.GetAllAsync(
                        filter: ca => ca.StudentId == reporterStudentId && ca.ApprovalStatus == ApprovalStatus.Approved && ca.DeletedAt == null
                    );
                    var reporterClassIds = reporterClassAssigns.Select(ca => ca.ClassId).ToList();

                    if (targetUser.RoleName == "Tutor")
                    {
                        var targetTutorId = await _uow.TutorProfiles.GetIdByUserIdAsync(targetUserId);
                        if (targetTutorId != null)
                        {
                            // Check if any of reporter's classes have this tutor
                            var classes = await _uow.Classes.GetAllAsync(
                                filter: c => reporterClassIds.Contains(c.Id) && c.TutorId == targetTutorId && c.DeletedAt == null
                            );
                            hasSharedClass = classes.Any();
                        }
                    }
                    else if (targetUser.RoleName == "Student")
                    {
                        var targetStudentId = await _uow.StudentProfiles.GetIdByUserIdAsync(targetUserId);
                        if (targetStudentId != null)
                        {
                            // Get all class IDs where target is approved
                            var targetClassAssigns = await _uow.ClassAssigns.GetAllAsync(
                                filter: ca => ca.StudentId == targetStudentId && ca.ApprovalStatus == ApprovalStatus.Approved && ca.DeletedAt == null
                            );
                            var targetClassIds = targetClassAssigns.Select(ca => ca.ClassId).ToList();
                            
                            // Check if they share any class
                            hasSharedClass = reporterClassIds.Any(id => targetClassIds.Contains(id));
                        }
                    }
                }
            }
            else if (reporter.RoleName == "Tutor")
            {
                var reporterTutorId = await _uow.TutorProfiles.GetIdByUserIdAsync(reporterUserId);
                if (reporterTutorId != null)
                {
                    if (targetUser.RoleName == "Student")
                    {
                        var targetStudentId = await _uow.StudentProfiles.GetIdByUserIdAsync(targetUserId);
                        if (targetStudentId != null)
                        {
                            // Get all class IDs where target student is approved
                            var targetClassAssigns = await _uow.ClassAssigns.GetAllAsync(
                                filter: ca => ca.StudentId == targetStudentId && ca.ApprovalStatus == ApprovalStatus.Approved && ca.DeletedAt == null
                            );
                            var targetClassIds = targetClassAssigns.Select(ca => ca.ClassId).ToList();

                            // Check if tutor teaches any of these classes
                            var classes = await _uow.Classes.GetAllAsync(
                                filter: c => targetClassIds.Contains(c.Id) && c.TutorId == reporterTutorId && c.DeletedAt == null
                            );
                            hasSharedClass = classes.Any();
                        }
                    }
                }
            }

            if (!hasSharedClass)
                throw new UnauthorizedAccessException("Bạn chỉ có thể báo cáo người dùng cùng lớp với bạn.");

            // 5. Create report
            var report = new Report
            {
                Id = Guid.NewGuid().ToString(),
                ReporterId = reporterUserId,
                TargetUserId = targetUserId,
                TargetLessonId = null,
                TargetMediaId = null,
                Description = $"[User:{targetUser.Email}] {reason}",
                Status = ReportStatus.Pending,
                CreatedAt = DateTimeHelper.GetVietnamTime()
            };

            await _uow.Reports.CreateAsync(report);
            await _uow.SaveChangesAsync();

            // 6. Notify Admin
            try
            {
                var adminRole = await _uow.Roles.GetAsync(r => r.RoleName == RoleEnum.Admin);
                if (adminRole != null)
                {
                    var adminUsers = await _uow.Users.GetAllAsync(filter: u => u.RoleId == adminRole.Id && u.DeletedAt == null);
                    foreach (var admin in adminUsers)
                    {
                        await _notificationService.CreateSystemAnnouncementNotificationAsync(
                            admin.Id,
                            "Báo cáo người dùng mới",
                            $"{reporter.Email} đã báo cáo {targetUser.Email}: {reason}",
                            report.Id
                        );
                    }
                }
            }
            catch { /* Log but don't fail */ }

            return report.Id;
        }

        public async Task<string> ReportLessonAsync(string reporterUserId, string lessonId, string reason)
        {
            // 1. Check rate limit (max 3 lesson reports/day)
            var since = DateTimeHelper.GetVietnamTime().AddDays(-1);
            var lessonReportCount = await _uow.Reports.GetAllAsync(
                filter: r => r.ReporterId == reporterUserId 
                    && r.TargetLessonId != null 
                    && r.CreatedAt >= since 
                    && r.DeletedAt == null
            );
            if (lessonReportCount.Count() >= 3)
                throw new InvalidOperationException("Bạn đã đạt giới hạn 3 báo cáo buổi học mỗi ngày.");

            // 2. Validate lesson exists
            var (lesson, cls) = await _uow.Lessons.GetWithClassAsync(lessonId);

            // 3. Validate reporter enrolled in class
            var reporter = await _uow.Users.GetByIdAsync(reporterUserId)
                ?? throw new KeyNotFoundException("Không tìm thấy thông tin người báo cáo.");

            bool isEnrolled = false;
            if (reporter.RoleName == "Student")
            {
                var studentProfileId = await _uow.StudentProfiles.GetIdByUserIdAsync(reporterUserId);
                if (studentProfileId != null)
                {
                    isEnrolled = await _uow.ClassAssigns.IsApprovedAsync(cls.Id, studentProfileId);
                }
            }
            else if (reporter.RoleName == "Tutor")
            {
                var tutorProfileId = await _uow.TutorProfiles.GetIdByUserIdAsync(reporterUserId);
                isEnrolled = cls.TutorId == tutorProfileId;
            }

            if (!isEnrolled)
                throw new UnauthorizedAccessException("Bạn không thuộc lớp này.");

            // 4. Check duplicate (không report cùng lesson trong 48h)
            var since48h = DateTimeHelper.GetVietnamTime().AddHours(-48);
            var hasDuplicate = await _uow.Reports.GetAllAsync(
                filter: r => r.ReporterId == reporterUserId 
                    && r.TargetLessonId == lessonId 
                    && r.CreatedAt >= since48h 
                    && r.DeletedAt == null
            );
            if (hasDuplicate.Any())
                throw new InvalidOperationException("Bạn đã báo cáo buổi học này trong vòng 48 giờ qua.");

            // 5. Create report
            var report = new Report
            {
                Id = Guid.NewGuid().ToString(),
                ReporterId = reporterUserId,
                TargetUserId = null,
                TargetLessonId = lessonId,
                TargetMediaId = null,
                Description = $"[Lesson:{lesson.Title}] {reason}",
                Status = ReportStatus.Pending,
                CreatedAt = DateTimeHelper.GetVietnamTime()
            };

            await _uow.Reports.CreateAsync(report);
            await _uow.SaveChangesAsync();

            // 6. Notify Admin
            try
            {
                var adminRole = await _uow.Roles.GetAsync(r => r.RoleName == RoleEnum.Admin);
                if (adminRole != null)
                {
                    var adminUsers = await _uow.Users.GetAllAsync(filter: u => u.RoleId == adminRole.Id && u.DeletedAt == null);
                    foreach (var admin in adminUsers)
                    {
                        await _notificationService.CreateSystemAnnouncementNotificationAsync(
                            admin.Id,
                            "Báo cáo buổi học mới",
                            $"{reporter.Email} đã báo cáo buổi học '{lesson.Title}': {reason}",
                            report.Id
                        );
                    }
                }
            }
            catch { /* Log but don't fail */ }

            return report.Id;
        }

        // Helper methods for validation
        private async Task CheckDuplicateReportAsync(string studentUserId, string mediaId, int hoursWindow = 24)
        {
            var since = DateTimeHelper.GetVietnamTime().AddHours(-hoursWindow);
            var hasDuplicate = await _uow.Reports.HasRecentReportAsync(studentUserId, mediaId, since);

            if (hasDuplicate)
            {
                throw new InvalidOperationException(
                    $"Bạn đã report tài liệu này trong vòng {hoursWindow} giờ qua. Vui lòng chờ xử lý.");
            }
        }

        private async Task CheckRateLimitAsync(string studentUserId, int maxReportsPerDay = 10)
        {
            var since = DateTimeHelper.GetVietnamTime().AddDays(-1);
            var count = await _uow.Reports.CountDistinctMaterialsReportedAsync(studentUserId, since);

            if (count >= maxReportsPerDay)
            {
                throw new InvalidOperationException(
                    $"Bạn đã đạt giới hạn {maxReportsPerDay} tài liệu được report mỗi ngày. Vui lòng thử lại sau.");
            }
        }

        public async Task<ApiResponse<bool>> RecordStudentResponseAsync(string token, string action)
        {
            // 1. Validate token
            var tokenData = _tokenService.ValidateStudentResponseToken(token);
            if (tokenData == null)
            {
                return ApiResponse<bool>.Fail("Token không hợp lệ hoặc đã hết hạn");
            }

            var (reportId, studentUserId) = tokenData.Value;

            // 2. Parse action
            if (action != "continue" && action != "cancel")
            {
                return ApiResponse<bool>.Fail("Hành động không hợp lệ. Chỉ chấp nhận 'continue' hoặc 'cancel'");
            }

            var studentResponse = action == "continue" 
                ? StudentResponseAction.Continue 
                : StudentResponseAction.Cancel;

            // 3. Get report and validate
            var report = await _uow.Reports.GetAsync(r => r.Id == reportId && r.DeletedAt == null);
            if (report == null)
            {
                return ApiResponse<bool>.Fail("Không tìm thấy báo cáo");
            }

            // Check if student already responded
            if (report.StudentResponse != null)
            {
                return ApiResponse<bool>.Fail("Bạn đã phản hồi báo cáo này rồi");
            }

            // Verify student ownership
            if (report.ReporterId != studentUserId)
            {
                return ApiResponse<bool>.Fail("Bạn không có quyền phản hồi báo cáo này");
            }

            // 4. Update report with student response
            report.StudentResponse = studentResponse;
            report.StudentRespondedAt = DateTimeHelper.VietnamNow;
            report.UpdatedAt = DateTimeHelper.VietnamNow;

            await _uow.Reports.UpdateAsync(report);
            await _uow.SaveChangesAsync();

            // 5. Send notification to admin
            try
            {
                var adminRole = await _uow.Roles.GetAsync(r => r.RoleName == RoleEnum.Admin);
                if (adminRole != null)
                {
                    var adminUsers = await _uow.Users.GetAllAsync(filter: u => u.RoleId == adminRole.Id && u.DeletedAt == null);
                    var actionText = action == "continue" ? "tiếp tục học" : "hủy lớp và hoàn tiền";
                    
                    foreach (var admin in adminUsers)
                    {
                        await _notificationService.CreateSystemAnnouncementNotificationAsync(
                            admin.Id,
                            "Phản hồi báo cáo vắng học",
                            $"📬 Học sinh đã phản hồi báo cáo vắng học: Chọn \"{actionText}\". Click để xem chi tiết.",
                            reportId
                        );
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to send admin notification: {ex.Message}");
            }

            var message = action == "continue"
                ? "Cảm ơn bạn đã xác nhận tiếp tục học. Chúng tôi khuyến khích bạn cải thiện tỷ lệ tham gia lớp học."
                : "Chúng tôi đã ghi nhận yêu cầu hủy lớp của bạn. Bộ phận hỗ trợ sẽ liên hệ với bạn sớm.";

            return ApiResponse<bool>.Ok(true, message);
        }
    }
}
