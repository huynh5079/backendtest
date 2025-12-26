using BusinessLayer.DTOs.Schedule.ScheduleEntry;
using BusinessLayer.Service.Interface;
using BusinessLayer.DTOs.Profile;
using BusinessLayer.Service.Interface.IScheduleService;
using DataLayer.Entities;
using DataLayer.Enum;
using DataLayer.Repositories.Abstraction.Schedule;
using DataLayer.Repositories.GenericType.Abstraction;
using DataLayer.Helper;
using Microsoft.EntityFrameworkCore;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Text;
using System.Threading.Tasks;
using static Microsoft.EntityFrameworkCore.DbLoggerCategory;

namespace BusinessLayer.Service.ScheduleService
{
    public class ScheduleViewService : IScheduleViewService
    {
        private readonly IScheduleUnitOfWork _uow;
        private readonly IStudentProfileService _studentProfileService;
        private readonly IParentChildrenService _parentChildrenService;

        public ScheduleViewService(
            IScheduleUnitOfWork uow,
            IStudentProfileService studentProfileService,
            IParentChildrenService parentChildrenService)
        {
            _uow = uow;
            _studentProfileService = studentProfileService; 
            _parentChildrenService = parentChildrenService;
        }

        public async Task<IEnumerable<ScheduleEntryDto>> GetTutorScheduleAsync(
            string tutorId, 
            DateTime startDate, 
            DateTime endDate, 
            string? entryType,
            string? classId = null,
            string? filterByStudentId = null, 
            string? classStatus = null,
            string? classMode = null)
        {
            //  startDate, endDate to UTC, endDate is full day
            var startUtc = startDate.Date.ToUniversalTime();
            // endDate ưill be 00:00 the next day in UTC
            var endUtc = endDate.Date.AddDays(1).ToUniversalTime();

            // Parse EntryType
            EntryType? targetEntryType = null;
            if (!string.IsNullOrEmpty(entryType) && Enum.TryParse<EntryType>(entryType.ToUpper(), out var type))
            {
                targetEntryType = type;
            }
            // Parse ClassStatus
            ClassStatus? targetClassStatus = null;
            if (!string.IsNullOrEmpty(classStatus) && Enum.TryParse<ClassStatus>(classStatus, true, out var status))
            {
                targetClassStatus = status;
            }
            // Parse ClassMode
            ClassMode? targetClassMode = null;
            if (!string.IsNullOrEmpty(classMode) && Enum.TryParse<ClassMode>(classMode, true, out var mode))
            {
                targetClassMode = mode;
            }
            // Apply classId filter if provided
            // Only applies to LESSON entries, BLOCK entries don't have Lesson/Class
            Expression<Func<ScheduleEntry, bool>> filter = se =>
                se.TutorId == tutorId &&
                se.DeletedAt == null &&
                se.StartTime < endUtc &&
                se.EndTime > startUtc &&
                // Filter Class
                (string.IsNullOrEmpty(classId) || (se.Lesson != null && se.Lesson.ClassId == classId)) &&
                // Filter EntryType
                (targetEntryType == null || se.EntryType == targetEntryType) &&
                // Filter Student
                (string.IsNullOrEmpty(filterByStudentId) || (se.Lesson != null && se.Lesson.Class.ClassAssigns.Any(ca => ca.StudentId == filterByStudentId))) &&
                (targetClassStatus == null || (se.Lesson != null && se.Lesson.Class.Status == targetClassStatus)) &&
                (targetClassMode == null || (se.Lesson != null && se.Lesson.Class.Mode == targetClassMode));

            // define includes - Tối ưu query để tránh timeout
            Func<IQueryable<ScheduleEntry>, IQueryable<ScheduleEntry>> includes = query =>
                query.Include(se => se.Lesson)
                        .ThenInclude(l => l.Class)
                            .ThenInclude(c => c.ClassAssigns) // Include to check if belong to this class
                     .Include(se => se.Block)
                     .AsNoTracking() // Tối ưu: không track entities để tăng performance
                     .OrderBy(se => se.StartTime);

            // Query
            var scheduleEntries = await _uow.ScheduleEntries.GetAllAsync(
                filter: filter,
                includes: includes
            );

            // Map DTO - Convert UTC to Vietnam time for frontend display
            // LƯU Ý: Dữ liệu mới được lưu ở UTC (StartTimeUtc từ ScheduleGenerationService)
            // Dữ liệu cũ có thể đã được lưu ở UTC hoặc Vietnam time
            // Logic hiện tại: Assume Unspecified = UTC (đúng cho dữ liệu mới)
            // Nếu dữ liệu cũ đã lưu ở Vietnam time, có thể bị convert sai (cộng thêm 7 giờ)
            // Giải pháp: Nếu phát hiện lỗi, cần migration để convert dữ liệu cũ sang UTC
            return scheduleEntries.Select(se => new ScheduleEntryDto
            {
                Id = se.Id,
                TutorId = se.TutorId,
                // Convert UTC to Vietnam time (assume UTC if Kind is Unspecified, as stored in DB)
                // Dữ liệu mới: StartTime được lưu từ StartTimeUtc (UTC) → convert đúng
                // Dữ liệu cũ: Nếu đã lưu ở UTC → convert đúng, nếu đã lưu ở Vietnam time → có thể sai
                StartTime = se.StartTime.Kind == DateTimeKind.Utc || se.StartTime.Kind == DateTimeKind.Unspecified
                    ? DateTimeHelper.ToVietnamTime(DateTime.SpecifyKind(se.StartTime, DateTimeKind.Utc))
                    : se.StartTime,
                EndTime = se.EndTime.Kind == DateTimeKind.Utc || se.EndTime.Kind == DateTimeKind.Unspecified
                    ? DateTimeHelper.ToVietnamTime(DateTime.SpecifyKind(se.EndTime, DateTimeKind.Utc))
                    : se.EndTime,
                EntryType = se.EntryType,
                LessonId = se.LessonId,
                ClassId = se.Lesson?.ClassId,
                Title = se.EntryType == EntryType.LESSON ? se.Lesson?.Title : "Lịch rảnh",
                AttendanceStatus = null,
                ClassMode = se.Lesson?.Class?.Mode.ToString(),
            });
        }

        public async Task<IEnumerable<ScheduleEntryDto>> GetStudentScheduleAsync(
            string studentUserId, 
            DateTime startDate, 
            DateTime endDate,
            string? filterByTutorId = null, 
            string? classStatus = null,
            string? classMode = null)
        {
            var studentProfileId = await _studentProfileService.GetStudentProfileIdByUserIdAsync(studentUserId);
            if (string.IsNullOrEmpty(studentProfileId))
                throw new UnauthorizedAccessException("Tài khoản học sinh không hợp lệ.");

            // Reuse logic nội bộ để tránh lặp code và đảm bảo ClassStatus được xử lý
            return await GetScheduleByStudentProfileIdInternal(studentProfileId, startDate, endDate, filterByTutorId, classStatus, classMode);
        }

        public async Task<IEnumerable<ScheduleEntryDto>> GetChildScheduleAsync(
            string parentUserId,
            string childProfileId,
            DateTime startDate,
            DateTime endDate,
            string? filterByTutorId = null,
            string? classStatus = null,
            string? classMode = null)
        {
            // validate parent-child relationship
            var isChild = await _parentChildrenService.IsChildOfParentAsync(parentUserId, childProfileId);
            if (!isChild)
            {
                throw new UnauthorizedAccessException("Bạn không có quyền xem lịch của học sinh này.");
            }

            // 2. Lấy UserId của đứa trẻ (Vì hàm GetStudentScheduleAsync đang nhận UserId)
            // Cần thêm hàm GetUserIdByProfileId trong StudentProfileService nếu chưa có.
            // HOẶC: Sửa GetStudentScheduleAsync để nhận ProfileId trực tiếp (tốt hơn).

            // Cách nhanh nhất hiện tại: Reuse logic query của GetStudentScheduleAsync nhưng viết lại đoạn đầu
            // Để tránh phụ thuộc UserId, mình sẽ tách logic query ra private method (Refactoring).

            return await GetScheduleByStudentProfileIdInternal(childProfileId, startDate, endDate, filterByTutorId, classStatus, classMode);
        }

        public async Task<IEnumerable<ScheduleEntryDto>> GetAllChildrenScheduleAsync(
            string parentUserId,
            DateTime startDate,
            DateTime endDate,
            string? filterByTutorId = null,
            string? classStatus = null,
            string? classMode = null)
        {
            // take all children IDs of this parent
            var childrenIds = await _parentChildrenService.GetChildrenInfoByParentUserIdAsync(parentUserId);

            if (!childrenIds.Any()) return new List<ScheduleEntryDto>();

            var allSchedules = new List<ScheduleEntryDto>();

            // loop through each child and get their schedule
            // can be optimized with when parallel tasks if needed
            foreach (var child in childrenIds)
            {
                var childSchedule = await GetScheduleByStudentProfileIdInternal(child.StudentId, startDate, endDate, filterByTutorId, classStatus, classMode);

                // create new list to avoid modifying reference
                var labeledSchedule = childSchedule.Select(s =>
                {
                    // Clone
                    return new ScheduleEntryDto
                    {
                        Id = s.Id,
                        TutorId = s.TutorId,
                        StartTime = s.StartTime,
                        EndTime = s.EndTime,
                        EntryType = s.EntryType,
                        LessonId = s.LessonId,
                        ClassId = s.ClassId,
                        AttendanceStatus = s.AttendanceStatus,
                        Title = $"[{child.FullName}] {s.Title}",
                        ClassMode = s.ClassMode
                    };
                });

                allSchedules.AddRange(childSchedule);
            }

            // Sort all entries by StartTime before returning
            return allSchedules.OrderBy(s => s.StartTime);
        }

        // query based on user directly
        private async Task<IEnumerable<ScheduleEntryDto>> GetScheduleByStudentProfileIdInternal(
            string studentProfileId,
            DateTime startDate,
            DateTime endDate,
            string? filterByTutorId = null, 
            string? classStatus = null,
            string? classMode = null)
        {
            var startUtc = startDate.Date.ToUniversalTime();
            var endUtc = endDate.Date.AddDays(1).ToUniversalTime();

            // Parse ClassStatus
            ClassStatus? targetClassStatus = null;
            if (!string.IsNullOrEmpty(classStatus) && Enum.TryParse<ClassStatus>(classStatus, true, out var status))
            {
                targetClassStatus = status;
            }

            // Parse ClassMode
            ClassMode? targetClassMode = null;
            if (!string.IsNullOrEmpty(classMode) && Enum.TryParse<ClassMode>(classMode, true, out var mode))
            {
                targetClassMode = mode;
            }

            // 2. Lấy danh sách ClassIds mà học sinh đã thanh toán (PaymentStatus = Paid)
            // Tránh vấn đề với Any() trên navigation property bằng cách query trực tiếp
            var paidClassIds = await _uow.ClassAssigns.GetAllAsync(
                filter: ca => ca.StudentId == studentProfileId &&
                             ca.PaymentStatus == PaymentStatus.Paid &&
                             ca.DeletedAt == null
            );
            var paidClassIdList = paidClassIds.Select(ca => ca.ClassId).Where(id => id != null).ToList();
            
            Console.WriteLine($"[GetScheduleByStudentProfileIdInternal] Học sinh {studentProfileId} đã thanh toán {paidClassIdList.Count} lớp:");
            foreach (var classId in paidClassIdList)
            {
                Console.WriteLine($"  - ClassId: {classId}");
            }

            // 3. Nếu không có lớp nào đã thanh toán, trả về empty
            if (!paidClassIdList.Any())
            {
                Console.WriteLine($"[GetScheduleByStudentProfileIdInternal] Học sinh {studentProfileId} chưa thanh toán lớp nào, trả về empty list");
                return new List<ScheduleEntryDto>();
            }

            // 4. Filter ScheduleEntries theo ClassIds đã thanh toán
            var scheduleEntries = await _uow.ScheduleEntries.GetAllAsync(
                filter: se =>
                    se.DeletedAt == null &&
                    se.EntryType == EntryType.LESSON &&
                    se.StartTime < endUtc &&
                    se.EndTime > startUtc &&
                    // Chỉ lấy lịch học của các lớp mà học sinh đã thanh toán
                    se.Lesson != null &&
                    se.Lesson.ClassId != null &&
                    paidClassIdList.Contains(se.Lesson.ClassId) &&
                    // filter by TutorId if provided
                    (string.IsNullOrEmpty(filterByTutorId) || se.TutorId == filterByTutorId) &&
                    // Check Class Status
                    (targetClassStatus == null || (se.Lesson.Class != null && se.Lesson.Class.Status == targetClassStatus)) &&
                    // Check Class Mode
                    (targetClassMode == null || (se.Lesson.Class != null && se.Lesson.Class.Mode == targetClassMode)),

                includes: query => query
                    .Include(se => se.Lesson)
                        .ThenInclude(l => l.Class)
                    .Include(se => se.Lesson)
                        .ThenInclude(l => l.Attendances)
                    .AsNoTracking() // Tối ưu: không track entities để tăng performance
                    .OrderBy(se => se.StartTime)
            );

            // Debug: Log để kiểm tra - SAU KHI FILTER
            Console.WriteLine($"[GetScheduleByStudentProfileIdInternal] Tìm thấy {scheduleEntries.Count()} schedule entries cho học sinh {studentProfileId}");
            Console.WriteLine($"  - Date range: {startDate:yyyy-MM-dd} đến {endDate:yyyy-MM-dd}");
            Console.WriteLine($"  - Filter by ClassStatus: {(targetClassStatus?.ToString() ?? "null (lấy cả Pending và Ongoing)")}");
            Console.WriteLine($"  - Filter by ClassMode: {(targetClassMode?.ToString() ?? "null (lấy tất cả)")}");
            
            // Debug: Kiểm tra ClassStatus của các lớp được trả về
            if (scheduleEntries.Any())
            {
                var firstEntry = scheduleEntries.First();
                var lastEntry = scheduleEntries.Last();
                Console.WriteLine($"  - First entry: {firstEntry.StartTime:yyyy-MM-dd HH:mm:ss}, ClassId: {firstEntry.Lesson?.ClassId}, ClassStatus: {firstEntry.Lesson?.Class?.Status}");
                Console.WriteLine($"  - Last entry: {lastEntry.StartTime:yyyy-MM-dd HH:mm:ss}, ClassId: {lastEntry.Lesson?.ClassId}, ClassStatus: {lastEntry.Lesson?.Class?.Status}");
                
                // Đếm số lượng entries theo ClassStatus
                var entriesByStatus = scheduleEntries
                    .GroupBy(se => se.Lesson?.Class?.Status?.ToString() ?? "Unknown")
                    .Select(g => new { Status = g.Key, Count = g.Count() })
                    .ToList();
                
                Console.WriteLine($"  - Phân bố theo ClassStatus:");
                foreach (var group in entriesByStatus)
                {
                    Console.WriteLine($"    - {group.Status}: {group.Count} entries");
                }
            }
            else
            {
                Console.WriteLine($"  - ⚠️ KHÔNG tìm thấy schedule entries nào!");
                Console.WriteLine($"  - Có thể do:");
                Console.WriteLine($"    1. Học sinh chưa thanh toán (PaymentStatus != Paid) - Đã kiểm tra: {paidClassIdList.Count} lớp đã thanh toán");
                Console.WriteLine($"    2. Không có lessons/schedule entries trong date range cho các lớp đã thanh toán");
                Console.WriteLine($"    3. ClassStatus filter (nếu có)");
                Console.WriteLine($"    4. ClassMode filter (nếu có)");
            }

            return scheduleEntries.Select(se =>
            {
                var myAttendance = se.Lesson?.Attendances?
                    .FirstOrDefault(a => a.StudentId == studentProfileId);
                return new ScheduleEntryDto
                {
                    Id = se.Id,
                    TutorId = se.TutorId,
                    // Convert UTC to Vietnam time (assume UTC if Kind is Unspecified, as stored in DB)
                    StartTime = se.StartTime.Kind == DateTimeKind.Utc || se.StartTime.Kind == DateTimeKind.Unspecified
                        ? DateTimeHelper.ToVietnamTime(DateTime.SpecifyKind(se.StartTime, DateTimeKind.Utc))
                        : se.StartTime,
                    EndTime = se.EndTime.Kind == DateTimeKind.Utc || se.EndTime.Kind == DateTimeKind.Unspecified
                        ? DateTimeHelper.ToVietnamTime(DateTime.SpecifyKind(se.EndTime, DateTimeKind.Utc))
                        : se.EndTime,
                    EntryType = se.EntryType,
                    LessonId = se.LessonId,
                    ClassId = se.Lesson?.ClassId,
                    Title = se.Lesson?.Title,
                    AttendanceStatus = myAttendance?.Status.ToString(),
                    ClassMode = se.Lesson?.Class?.Mode.ToString()
                };
            });
        }
    }
}
