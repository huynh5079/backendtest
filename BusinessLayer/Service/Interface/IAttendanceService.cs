using BusinessLayer.DTOs.Attendance;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace BusinessLayer.Service.Interface
{
    public interface IAttendanceService
    {
        // Tutor thao tác
        Task<AttendanceRecordDto> MarkAsync(string tutorUserId, MarkAttendanceRequest req);
        Task<List<AttendanceRecordDto>> BulkMarkForLessonAsync(string tutorUserId, string lessonId, Dictionary<string, string> studentStatusMap, string? notes = null);

        // Xem trên lịch (tutor)
        Task<IEnumerable<ScheduleCellAttendanceDto>> GetTutorAttendanceOnCalendarAsync(string tutorUserId, DateTime start, DateTime end);

        // Xem trên lịch (student/parent)
        Task<IEnumerable<ScheduleCellAttendanceDto>> GetStudentAttendanceOnCalendarAsync(string studentUserId, DateTime start, DateTime end);

        // Phụ huynh xem theo “con”
        Task<IEnumerable<ScheduleCellAttendanceDto>> GetParentChildAttendanceCalendarAsync(string parentUserId, string studentId, DateTime start, DateTime end);

        // Chi tiết 1 buổi (lesson) cho tutor
        Task<IEnumerable<AttendanceRecordDto>> GetLessonAttendanceAsync(string tutorUserId, string lessonId);

        Task<List<LessonRosterItemDto>> GetLessonRosterForTutorAsync(string tutorUserId, string lessonId);

        // ========== NEW: Class-Based Attendance Methods ==========

        // Tutor: Get overview của class (both tabs)
        Task<ClassAttendanceOverviewDto> GetClassAttendanceOverviewAsync(string tutorUserId, string classId);

        // Tutor: Drill down student
        Task<StudentAttendanceDetailDto> GetStudentAttendanceInClassAsync(string tutorUserId, string classId, string studentId);

        // Tutor: Drill down lesson
        Task<LessonAttendanceDetailDto> GetLessonAttendanceDetailAsync(string tutorUserId, string lessonId);

        // Student: My attendance in class
        Task<StudentAttendanceDetailDto> GetMyClassAttendanceAsync(string studentUserId, string classId);

        // Parent: Child attendance in class
        Task<StudentAttendanceDetailDto> GetChildClassAttendanceAsync(string parentUserId, string studentId, string classId);
    }
}
