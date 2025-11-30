using BusinessLayer.DTOs.Schedule.ScheduleEntry;
using BusinessLayer.Service.Interface;
using BusinessLayer.Service.Interface.IScheduleService;
using DataLayer.Entities;
using DataLayer.Enum;
using DataLayer.Repositories.Abstraction.Schedule;
using DataLayer.Repositories.GenericType.Abstraction;
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
            string? filterByStudentId = null)
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
                (string.IsNullOrEmpty(filterByStudentId) || (se.Lesson != null && se.Lesson.Class.ClassAssigns.Any(ca => ca.StudentId == filterByStudentId)));

            // define includes
            Func<IQueryable<ScheduleEntry>, IQueryable<ScheduleEntry>> includes = query =>
                query.Include(se => se.Lesson)
                        .ThenInclude(l => l.Class)
                            .ThenInclude(c => c.ClassAssigns) // Include to check if belong to this class
                     .Include(se => se.Block)
                     .OrderBy(se => se.StartTime);

            // Query
            var scheduleEntries = await _uow.ScheduleEntries.GetAllAsync(
                filter: filter,
                includes: includes
            );

            // Map DTO
            return scheduleEntries.Select(se => new ScheduleEntryDto
            {
                Id = se.Id,
                TutorId = se.TutorId,
                StartTime = se.StartTime, // Return to UTC
                EndTime = se.EndTime,     // Return to UTC
                EntryType = se.EntryType,
                LessonId = se.LessonId,
                ClassId = se.Lesson?.ClassId,
                Title = se.EntryType == EntryType.LESSON ? se.Lesson?.Title : "Lịch rảnh",
                AttendanceStatus = null
            });
        }

        public async Task<IEnumerable<ScheduleEntryDto>> GetStudentScheduleAsync(
            string studentUserId, 
            DateTime startDate, 
            DateTime endDate,
            string? filterByTutorId = null)
        {
            // take studentUserId, get StudentProfileId
            var studentProfileId = await _studentProfileService.GetStudentProfileIdByUserIdAsync(studentUserId);
            if (string.IsNullOrEmpty(studentProfileId))
                throw new UnauthorizedAccessException("Tài khoản học sinh không hợp lệ.");

            // change startDate, endDate to UTC, endDate is full day
            var startUtc = startDate.Date.ToUniversalTime();
            var endUtc = endDate.Date.AddDays(1).ToUniversalTime();

            // qyuery ScheduleEntries
            var scheduleEntries = await _uow.ScheduleEntries.GetAllAsync(
                filter: se =>
                    se.DeletedAt == null &&
                    se.EntryType == EntryType.LESSON && // student only see LESSON
                    se.StartTime < endUtc &&
                    se.EndTime > startUtc &&
                    // check if the Lesson's Class has an assignment for this student
                    se.Lesson.Class.ClassAssigns.Any(ca => ca.StudentId == studentProfileId) &&
                    (string.IsNullOrEmpty(filterByTutorId) || se.TutorId == filterByTutorId),

                includes: query => query
                    .Include(se => se.Lesson)
                        .ThenInclude(l => l.Class)
                            .ThenInclude(c => c.ClassAssigns)
                    .Include(se => se.Lesson)
                        .ThenInclude(l => l.Attendances)
                    .OrderBy(se => se.StartTime)
            );
                
            // map to DTO
            return scheduleEntries.Select(se =>
            {
                // Find attendance record for this student in the lesson
                var myAttendance = se.Lesson?.Attendances?
                    .FirstOrDefault(a => a.StudentId == studentProfileId);

                return new ScheduleEntryDto
                {
                    Id = se.Id,
                    TutorId = se.TutorId,
                    StartTime = se.StartTime,
                    EndTime = se.EndTime,
                    EntryType = se.EntryType,
                    LessonId = se.LessonId,
                    ClassId = se.Lesson?.ClassId,   // take from Lesson
                    Title = se.Lesson?.Title,   // take from Lesson
                    // Map attendance status if exists
                    AttendanceStatus = myAttendance?.Status.ToString()
                };
            });
        }

        public async Task<IEnumerable<ScheduleEntryDto>> GetChildScheduleAsync(
            string parentUserId,
            string childProfileId,
            DateTime startDate,
            DateTime endDate)
        {
            // 1. Validate: Đứa trẻ này có phải con của Parent không?
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

            return await GetScheduleByStudentProfileIdInternal(childProfileId, startDate, endDate);
        }

        public async Task<IEnumerable<ScheduleEntryDto>> GetAllChildrenScheduleAsync(
            string parentUserId,
            DateTime startDate,
            DateTime endDate)
        {
            // 1. Lấy danh sách ID các con
            var childrenIds = await _parentChildrenService.GetChildrenIdsByParentUserIdAsync(parentUserId);

            if (!childrenIds.Any()) return new List<ScheduleEntryDto>();

            var allSchedules = new List<ScheduleEntryDto>();

            // 2. Loop lấy lịch từng con
            // (Có thể dùng Task.WhenAll để chạy song song cho nhanh)
            foreach (var childId in childrenIds)
            {
                var childSchedule = await GetScheduleByStudentProfileIdInternal(childId, startDate, endDate);

                // (Optional) Gán thêm tên con vào Title để phụ huynh biết lịch của ai
                // var childName = ... load name ...
                // foreach(var s in childSchedule) s.Title = $"[{childName}] {s.Title}";

                allSchedules.AddRange(childSchedule);
            }

            // 3. Sắp xếp tổng hợp theo thời gian
            return allSchedules.OrderBy(s => s.StartTime);
        }

        // --- HELPER METHOD (Refactor từ GetStudentScheduleAsync) ---
        // Hàm này query dựa trên ProfileID (không cần UserId) -> Dễ reuse cho cả Student và Parent
        private async Task<IEnumerable<ScheduleEntryDto>> GetScheduleByStudentProfileIdInternal(
            string studentProfileId,
            DateTime startDate,
            DateTime endDate,
            string? filterByTutorId = null)
        {
            var startUtc = startDate.Date.ToUniversalTime();
            var endUtc = endDate.Date.AddDays(1).ToUniversalTime();

            var scheduleEntries = await _uow.ScheduleEntries.GetAllAsync(
                filter: se =>
                    se.DeletedAt == null &&
                    se.EntryType == EntryType.LESSON &&
                    se.StartTime < endUtc &&
                    se.EndTime > startUtc &&
                    se.Lesson.Class.ClassAssigns.Any(ca => ca.StudentId == studentProfileId) &&
                    (string.IsNullOrEmpty(filterByTutorId) || se.TutorId == filterByTutorId),

                includes: query => query
                    .Include(se => se.Lesson)
                        .ThenInclude(l => l.Class)
                            .ThenInclude(c => c.ClassAssigns)
                    .Include(se => se.Lesson)
                        .ThenInclude(l => l.Attendances)
                    .OrderBy(se => se.StartTime)
            );

            return scheduleEntries.Select(se =>
            {
                var myAttendance = se.Lesson?.Attendances?
                    .FirstOrDefault(a => a.StudentId == studentProfileId);
                return new ScheduleEntryDto
                {
                    Id = se.Id,
                    TutorId = se.TutorId,
                    StartTime = se.StartTime,
                    EndTime = se.EndTime,
                    EntryType = se.EntryType,
                    LessonId = se.LessonId,
                    ClassId = se.Lesson?.ClassId,
                    Title = se.Lesson?.Title,
                    AttendanceStatus = myAttendance?.Status.ToString()
                };
            });
        }
    }
}
