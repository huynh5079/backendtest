using BusinessLayer.Helper;
using BusinessLayer.Service.Interface;
using BusinessLayer.Service.Interface.IScheduleService;
using DataLayer.Entities;
using DataLayer.Enum;
using DataLayer.Repositories.Abstraction;
using DataLayer.Repositories.Abstraction.Schedule;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using System;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace BusinessLayer.Service
{
    public class ClassStatusCheckService : IClassStatusCheckService
    {
        private readonly IScheduleUnitOfWork _uow;
        private readonly ILogger<ClassStatusCheckService> _logger;
        private readonly IScheduleGenerationService _scheduleGenerationService;
        private readonly TpeduContext _context;

        private const int PENDING_CLASS_TIMEOUT_HOURS = 24;

        public ClassStatusCheckService(
            IScheduleUnitOfWork uow,
            ILogger<ClassStatusCheckService> logger,
            IScheduleGenerationService scheduleGenerationService,
            TpeduContext context)
        {
            _uow = uow;
            _logger = logger;
            _scheduleGenerationService = scheduleGenerationService;
            _context = context;
        }

        public async Task<int> CheckAndUpdateClassStatusAsync(CancellationToken ct = default)
        {
            int totalUpdated = 0;
            try
            {
                // Activate due classes
                totalUpdated += await ActivateDueClassesAsync(ct);

                // Cancel unpaid pending classes
                totalUpdated += await CleanupUnpaidClassesAsync(ct);

                if (totalUpdated > 0)
                {
                    await _uow.SaveChangesAsync();
                }

                return totalUpdated;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Lỗi xảy ra trong quá trình quét trạng thái lớp học.");
                throw;
            }
        }

        private async Task<int> ActivateDueClassesAsync(CancellationToken ct)
        {
            var now = DateTimeHelper.VietnamNow;

            // _uow.Classes giờ sẽ hoạt động vì IScheduleUnitOfWork có chứa Classes
            var dueClasses = await _uow.Classes.GetAllAsync(
                filter: c => c.DeletedAt == null &&
                             c.Status == ClassStatus.Pending &&
                             c.ClassStartDate != null &&
                             c.ClassStartDate <= now
            );

            int count = 0;
            foreach (var cls in dueClasses)
            {
                if (cls.CurrentStudentCount > 0)
                {
                    cls.Status = ClassStatus.Ongoing;
                    await _uow.Classes.UpdateAsync(cls);
                    _logger.LogInformation($"[AUTO-ACTIVATE] Lớp {cls.Id} ({cls.Title}) đã được chuyển sang Ongoing vì đến ngày khai giảng.");
                    
                    // Sinh lịch học nếu chưa có
                    try
                    {
                        await GenerateLessonsIfNeededAsync(cls, ct);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, $"[AUTO-ACTIVATE] Lỗi khi sinh lịch cho lớp {cls.Id}: {ex.Message}");
                        // Không throw để không rollback việc cập nhật status
                    }
                    
                    count++;
                }
            }
            return count;
        }

        private async Task<int> CleanupUnpaidClassesAsync(CancellationToken ct)
        {
            var now = DateTimeHelper.VietnamNow;
            var timeoutThreshold = now.AddHours(-PENDING_CLASS_TIMEOUT_HOURS);

            // Find all pending classes created before the timeout threshold
            var expiredClasses = await _uow.Classes.GetAllAsync(
                filter: c => c.DeletedAt == null &&
                             c.Status == ClassStatus.Pending &&
                             c.CreatedAt < timeoutThreshold
            );

            int count = 0;
            foreach (var cls in expiredClasses)
            {
                bool shouldCancel = false;
                string? originalRequestId = null;

                // Check if the class is linked to a ClassRequest
                //var match = Regex.Match(cls.Title ?? "", @"\(từ yêu cầu ([a-f0-9\-]+)\)");
                var match = Regex.Match(cls.Description ?? "", @"\[RefReqId:([a-f0-9\-]+)\]");

                if (match.Success)
                {
                    // If linked to a ClassRequest, always cancel
                    shouldCancel = true;
                    originalRequestId = match.Groups[1].Value;
                }
                else
                {
                    // KHÔNG tự động hủy lớp chỉ vì CurrentStudentCount == 0
                    // Lớp vẫn ở trạng thái Pending để học sinh có thể đăng ký
                    // Chỉ tutor hoặc admin mới có thể hủy lớp
                    // Không set shouldCancel = true
                }

                if (shouldCancel)
                {
                    // Cancel Class 
                    cls.Status = ClassStatus.Cancelled;
                    await _uow.Classes.UpdateAsync(cls);
                    count++;

                    // Restore original ClassRequest if applicable
                    if (!string.IsNullOrEmpty(originalRequestId))
                    {
                        var originalRequest = await _uow.ClassRequests.GetAsync(r => r.Id == originalRequestId);

                        if (originalRequest != null && originalRequest.Status == ClassRequestStatus.Matched)
                        {
                            // Restore ClassRequest to Pending
                            originalRequest.Status = ClassRequestStatus.Pending;
                            await _uow.ClassRequests.UpdateAsync(originalRequest);

                            // Cancel any accepted TutorApplication for this request
                            var acceptedApp = await _uow.TutorApplications.GetAsync(
                                ta => ta.ClassRequestId == originalRequestId &&
                                      ta.TutorId == cls.TutorId &&
                                      ta.Status == ApplicationStatus.Accepted);

                            if (acceptedApp != null)
                            {
                                acceptedApp.Status = ApplicationStatus.Cancelled;
                                await _uow.TutorApplications.UpdateAsync(acceptedApp);
                            }

                            _logger.LogInformation($"[AUTO-REOPEN] Đã hủy lớp {cls.Id} và mở lại Request {originalRequestId}.");
                        }
                    }
                    else
                    {
                        _logger.LogInformation($"[AUTO-CANCEL] Đã hủy lớp rác {cls.Id}.");
                    }
                }
            }
            return count;
        }

        /// <summary>
        /// Sinh lesson và schedule cho class nếu:
        /// - Class đang ở trạng thái Ongoing
        /// - Chưa có lesson nào trong DB
        /// </summary>
        private async Task GenerateLessonsIfNeededAsync(Class classEntity, CancellationToken ct)
        {
            if (classEntity.Status != ClassStatus.Ongoing)
            {
                return;
            }

            // Only generate if no lessons exist yet
            var hasLessons = await _context.Lessons.AnyAsync(l => l.ClassId == classEntity.Id, ct);
            if (hasLessons)
            {
                return;
            }

            // Load ClassSchedules nếu chưa có
            if (classEntity.ClassSchedules == null || !classEntity.ClassSchedules.Any())
            {
                var schedules = await _context.ClassSchedules
                    .Where(cs => cs.ClassId == classEntity.Id)
                    .ToListAsync(ct);
                classEntity.ClassSchedules = schedules;
            }

            if (classEntity.ClassSchedules != null && classEntity.ClassSchedules.Any())
            {
                // Gọi service sinh lịch
                await _scheduleGenerationService.GenerateScheduleFromClassAsync(
                    classEntity.Id,
                    classEntity.TutorId,
                    classEntity.ClassStartDate ?? DateTimeHelper.VietnamNow,
                    classEntity.ClassSchedules
                );
                _logger.LogInformation($"[AUTO-ACTIVATE] Đã sinh lịch học cho lớp {classEntity.Id}.");
            }
            else
            {
                _logger.LogWarning($"[AUTO-ACTIVATE] Lớp {classEntity.Id} không có ClassSchedules để sinh lịch.");
            }
        }
    }
}