using BusinessLayer.Helper;
using BusinessLayer.DTOs.Reports;
using BusinessLayer.Options;
using BusinessLayer.Service.Interface;
using DataLayer.Entities;
using DataLayer.Enum;
using DataLayer.Repositories.Abstraction;
using DataLayer.Repositories.Abstraction.Schedule;
using Microsoft.Extensions.Options;

namespace BusinessLayer.Service
{
    public class AutoReportService : IAutoReportService
    {
        private readonly IUnitOfWork _uow;
        private readonly IScheduleUnitOfWork _scheduleUow;
        private readonly IReportService _reportService;
        private readonly INotificationService _notificationService;
        private readonly IEmailService _emailService;
        private readonly ITokenService _tokenService;
        private readonly AutoReportSettings _settings;
        private readonly FrontendSettings _frontendSettings;

        public AutoReportService(
            IUnitOfWork uow,
            IScheduleUnitOfWork scheduleUow,
            IReportService reportService,
            INotificationService notificationService,
            IEmailService emailService,
            ITokenService tokenService,
            IOptions<AutoReportSettings> settings,
            IOptions<FrontendSettings> frontendSettings)
        {
            _uow = uow;
            _scheduleUow = scheduleUow;
            _reportService = reportService;
            _notificationService = notificationService;
            _emailService = emailService;
            _tokenService = tokenService;
            _settings =settings.Value;
            _frontendSettings = frontendSettings.Value;
        }

        public async Task<int> CheckAndCreateAutoReportsAsync(CancellationToken ct = default)
        {
            if (!_settings.EnableAutoReport)
                return 0;

            int reportsCreated = 0;

            // 1. Get all active/ongoing classes  
            var activeClasses = await _scheduleUow.Classes.GetAllAsync(
                filter: c => c.Status == ClassStatus.Ongoing && c.DeletedAt == null
            );

            foreach (var classEntity in activeClasses)
            {
                // 2. Get all approved students in class
                var classAssigns = await _uow.ClassAssigns.GetAllAsync(
                    filter: ca => ca.ClassId == classEntity.Id 
                               && ca.ApprovalStatus == ApprovalStatus.Approved 
                               && ca.StudentId != null
                );

                foreach (var assign in classAssigns)
                {
                    if (assign.StudentId == null) continue;

                    // 3. Check if student has excessive absences
                    var shouldReport = await ShouldCreateAutoReportAsync(
                        classEntity.Id!, 
                        assign.StudentId, 
                        ct
                    );

                    if (shouldReport)
                    {
                        // 4. Check for recent auto-report (prevent spam)
                        var hasRecent = await HasRecentAutoReportAsync(
                            classEntity.Id!, 
                            assign.StudentId, 
                            ct
                        );

                        if (!hasRecent)
                        {
                            // 5. Create auto-report
                            await CreateAutoReportForStudentAsync(
                                classEntity.Id!, 
                                assign.StudentId, 
                                ct
                            );
                            reportsCreated++;
                        }
                    }
                }
            }

            return reportsCreated;
        }

        private async Task<bool> ShouldCreateAutoReportAsync(
            string classId, 
            string studentId, 
            CancellationToken ct)
        {
            // Get all lessons in class
            var lessons = (await _scheduleUow.Lessons.GetByClassWithScheduleEntriesAsync(classId)).ToList();
            
            // Check min lessons threshold
            if (lessons.Count < _settings.MinLessonsBeforeCheck)
                return false;

            // Get student's attendance records
            var lessonIds = lessons.Select(l => l.Id).ToList();
            var attendances = (await _uow.Attendances.GetAttendancesByLessonIdsAsync(lessonIds))
                .Where(a => a.StudentId == studentId)
                .OrderBy(a => a.CreatedAt)
                .ToList();

            if (attendances.Count == 0)
                return false;

            // Calculate absence metrics
            int totalLessons = lessons.Count;
            int absentCount = attendances.Count(a => a.Status == AttendanceStatus.Absent);
            decimal absenceRate = totalLessons > 0 ? (decimal)absentCount / totalLessons : 0;

            // Check absence rate threshold (e.g., >30%)
            if (absenceRate > _settings.AbsenceRateThreshold)
                return true;

            // Check consecutive absences
            int consecutiveAbsences = 0;
            int maxConsecutive = 0;

            foreach (var attendance in attendances)
            {
                if (attendance.Status == AttendanceStatus.Absent)
                {
                    consecutiveAbsences++;
                    maxConsecutive = Math.Max(maxConsecutive, consecutiveAbsences);
                }
                else
                {
                    consecutiveAbsences = 0;
                }
            }

            // Check consecutive absence threshold (e.g., >=3)
            if (maxConsecutive >= _settings.ConsecutiveAbsenceThreshold)
                return true;

            return false;
        }

        private async Task<bool> HasRecentAutoReportAsync(
            string classId, 
            string studentId, 
            CancellationToken ct)
        {
            var since = DateTimeHelper.VietnamNow.AddDays(-_settings.DuplicateCheckWindowDays);
            
            return await _uow.Reports.HasRecentAutoReportAsync(studentId, classId, since);
        }

        private async Task CreateAutoReportForStudentAsync(
            string classId, 
            string studentId, 
            CancellationToken ct)
        {
            // Get class and student info
            var classEntity = await _scheduleUow.Classes.GetByIdAsync(classId);
            if (classEntity == null) return;

            var studentProfile = await _uow.StudentProfiles.GetByIdAsync(studentId);
            if (studentProfile == null || string.IsNullOrEmpty(studentProfile.UserId)) return;

            // Calculate absence stats
            var lessons = (await _scheduleUow.Lessons.GetByClassWithScheduleEntriesAsync(classId)).ToList();
            var lessonIds = lessons.Select(l => l.Id).ToList();
            var attendances = (await _uow.Attendances.GetAttendancesByLessonIdsAsync(lessonIds))
                .Where(a => a.StudentId == studentId)
                .ToList();

            int totalLessons = lessons.Count;
            int absentCount = attendances.Count(a => a.Status == AttendanceStatus.Absent);
            decimal absenceRate = totalLessons > 0 ? Math.Round((decimal)absentCount / totalLessons * 100, 2) : 0;

            // Create auto-report description
            var description = $"[AUTO-REPORT][ClassId:{classId}][StudentId:{studentId}] " +
                            $"Học sinh vắng {absentCount}/{totalLessons} buổi ({absenceRate}%)";

            // Create report
            var report = new Report
            {
                ReporterId = studentProfile.UserId, // Student auto-reports themselves
                TargetUserId = null, // Report to Admin
                TargetLessonId = null,
                TargetMediaId = null,
                Description = description,
                Status = ReportStatus.Pending,
                CreatedAt = DateTimeHelper.VietnamNow,
                UpdatedAt = DateTimeHelper.VietnamNow
            };

            await _uow.Reports.CreateAsync(report);
            await _uow.SaveChangesAsync();

            // Send notification to Admin
            try
            {
                // Get all admin users
                var adminRole = await _uow.Roles.GetAsync(r => r.RoleName == RoleEnum.Admin);
                if (adminRole != null)
                {
                    var adminUsers = await _uow.Users.GetAllAsync(filter: u => u.RoleId == adminRole.Id && u.DeletedAt == null);
                    
                    foreach (var admin in adminUsers)
                    {
                        await _notificationService.CreateSystemAnnouncementNotificationAsync(
                            admin.Id,
                            "Báo cáo vắng học tự động",
                            $"🚨 Học sinh trong lớp \"{classEntity.Title}\" có tỷ lệ vắng {absenceRate}%. Click để xem chi tiết.",
                            report.Id
                        );
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to send admin notification: {ex.Message}");
            }
            
            // Send email to student
            await SendAbsenceWarningEmailAsync(
                studentProfile, 
                classEntity, 
                absentCount, 
                totalLessons, 
                absenceRate,
                report.Id,
                ct
            );
        }

        private async Task SendAbsenceWarningEmailAsync(
            StudentProfile student,
            Class classEntity,
            int absentCount,
            int totalLessons,
            decimal absenceRate,
            string reportId,
            CancellationToken ct)
        {
            var studentUser = await _uow.Users.GetByIdAsync(student.UserId!);
            if (studentUser == null || string.IsNullOrEmpty(studentUser.Email))
                return;

            try
            {
                // Load email template
                var templatePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "EmailTemplates", "absence-warning.html");
                if (!File.Exists(templatePath))
                {
                    System.Diagnostics.Debug.WriteLine($"Email template not found: {templatePath}");
                    return;
                }

                var htmlTemplate = await File.ReadAllTextAsync(templatePath, ct);

                // Generate response token
                var responseToken = _tokenService.GenerateStudentResponseToken(reportId, student.UserId!);
                var frontendBaseUrl = _frontendSettings.GetStudentResponseUrl();

                // Replace tokens
                var html = htmlTemplate
                    .Replace("{{BRAND_NAME}}", "TPEdu Center")
                    .Replace("{{BRAND_URL}}", "#")
                    .Replace("{{LOGO_URL}}", "https://res.cloudinary.com/dwmfmq5xa/image/upload/v1758521309/plugins_email-verification-plugin_m7tyci.png")
                    .Replace("{{STUDENT_NAME}}", studentUser.UserName ?? "Học sinh")
                    .Replace("{{CLASS_NAME}}", classEntity.Title ?? "Lớp học")
                    .Replace("{{ABSENT_COUNT}}", absentCount.ToString())
                    .Replace("{{TOTAL_LESSONS}}", totalLessons.ToString())
                    .Replace("{{ABSENCE_RATE}}", absenceRate.ToString("0.##"))
                    .Replace("{{SUPPORT_EMAIL}}", "support@tpedu.vn")
                    .Replace("{{YEAR}}", DateTime.Now.Year.ToString())
                    .Replace("{{CONTINUE_CLASS_URL}}", $"{frontendBaseUrl}?token={responseToken}&action=continue")
                    .Replace("{{CANCEL_CLASS_URL}}", $"{frontendBaseUrl}?token={responseToken}&action=cancel");

                await _emailService.SendAsync(
                    studentUser.Email,
                    "⚠️ Thông báo: Tỷ lệ vắng học cao",
                    html
                );
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to send absence warning email: {ex.Message}");
            }
        }

        public async Task<AutoReportPagedResponse> GetAutoReportsAsync(AutoReportQuery query, CancellationToken ct = default)
        {
            // Validate pagination params
            query.Page = Math.Max(1, query.Page);
            query.PageSize = Math.Clamp(query.PageSize, 1, 100);

            // Parse response status filter
            bool? hasResponse = query.ResponseStatus?.ToLower() switch
            {
                "responded" => true,
                "pending" => false,
                _ => null // "all" or empty
            };

            // Delegate query to repository
            var (reports, totalCount) = await _uow.Reports.GetAutoReportsPagedAsync(
                classId: query.ClassId,
                studentId: query.StudentId,
                fromDate: query.FromDate,
                toDate: query.ToDate,
                hasResponse: hasResponse,
                sortBy: query.SortBy,
                sortDescending: query.SortOrder.ToLower() == "desc",
                page: query.Page,
                pageSize: query.PageSize
            );

            // Map to DTOs (only DTO mapping in service layer)
            var items = new List<AutoReportItemDto>();
            foreach (var report in reports)
            {
                // Parse description to extract metadata
                var description = report.Description ?? "";
                var classId = ExtractFromDescription(description, "ClassId");
                var studentId = ExtractFromDescription(description, "StudentId");
                var absentCount = ExtractNumberFromDescription(description, "absent count");
                var totalLessons = ExtractNumberFromDescription(description, "total lessons");

                // Get student name
                var student = await _uow.Users.GetAsync(u => u.Id == report.ReporterId);
                var studentName = student?.UserName ?? "Unknown Student";

                // Get class name
                var classEntity = await _scheduleUow.Classes.GetAsync(c => c.Id == classId);
                var className = classEntity?.Title ?? "Unknown Class";

                // Calculate absence rate
                var absenceRate = totalLessons > 0 ? (double)absentCount / totalLessons * 100 : 0;

                items.Add(new AutoReportItemDto
                {
                    Id = report.Id,
                    ReporterId = report.ReporterId ?? "",
                    ReporterName = studentName,
                    ClassId = classId ?? "",
                    ClassName = className,
                    AbsentCount = absentCount,
                    TotalLessons = totalLessons,
                    AbsenceRate = Math.Round(absenceRate, 2),
                    Status = report.Status,
                    StudentResponse = report.StudentResponse,
                    StudentRespondedAt = report.StudentRespondedAt,
                    CreatedAt = report.CreatedAt
                });
            }

            return new AutoReportPagedResponse
            {
                Items = items,
                TotalCount = totalCount,
                Page = query.Page,
                PageSize = query.PageSize
            };
        }

        // Helper methods to parse description
        private string? ExtractFromDescription(string description, string key)
        {
            var pattern = $"[{key}:";
            var startIndex = description.IndexOf(pattern);
            if (startIndex == -1) return null;

            startIndex += pattern.Length;
            var endIndex = description.IndexOf(']', startIndex);
            if (endIndex == -1) return null;

            return description.Substring(startIndex, endIndex - startIndex);
        }

        private int ExtractNumberFromDescription(string description, string context)
        {
            // Example: "Học sinh vắng 4/10 buổi"
            var parts = description.Split(' ');
            for (int i = 0; i < parts.Length; i++)
            {
                if (parts[i].Contains('/'))
                {
                    var numbers = parts[i].Split('/');
                    if (context.Contains("absent") && numbers.Length > 0)
                    {
                        if (int.TryParse(numbers[0], out int absent))
                            return absent;
                    }
                    if (context.Contains("total") && numbers.Length > 1)
                    {
                        if (int.TryParse(numbers[1], out int total))
                            return total;
                    }
                }
            }
            return 0;
        }
    }
}
