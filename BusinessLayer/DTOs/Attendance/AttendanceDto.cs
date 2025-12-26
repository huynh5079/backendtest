using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace BusinessLayer.DTOs.Attendance
{
    public class MarkAttendanceRequest
    {
        public string LessonId { get; set; } = default!;
        public string StudentId { get; set; } = default!;
        public string Status { get; set; } = default!; // "Present"/"Late"/"Absent"/"Excused"
        public string? Notes { get; set; }
    }

    public class AttendanceRecordDto
    {
        public string LessonId { get; set; } = default!;
        public string StudentId { get; set; } = default!;
        public string StudentName { get; set; } = default!;
        public string Status { get; set; } = default!;
        public string? Notes { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime? UpdatedAt { get; set; }
    }

    // Để overlay lên “lịch học”
    public class ScheduleCellAttendanceDto
    {
        public string ScheduleEntryId { get; set; } = default!;
        public string LessonId { get; set; } = default!;
        public DateTime StartTime { get; set; }
        public DateTime EndTime { get; set; }
        // Với lớp: tổng quát hoá
        public int TotalStudents { get; set; }
        public int Present { get; set; }
        public int Late { get; set; }
        public int Absent { get; set; }
        public int Excused { get; set; }

        // Với 1-1: FE có thể nhìn vào tổng số và quyết định “chip/badge”
    }

    // ========== NEW: Class-Based Attendance DTOs (Option C) ==========

    // Tab 1: Student summary (for tutor overview)
    public class StudentAttendanceSummaryDto
    {
        public string StudentId { get; set; } = default!;
        public string StudentUserId { get; set; } = default!;
        public string StudentName { get; set; } = default!;
        public string? Email { get; set; }
        public string? AvatarUrl { get; set; }

        public int TotalLessons { get; set; }
        public int PresentCount { get; set; }
        public int LateCount { get; set; }
        public int AbsentCount { get; set; }
        public int ExcusedCount { get; set; }
        public int NotMarkedCount { get; set; }
        public double AttendanceRate { get; set; } // Percentage
    }

    // Tab 2: Lesson summary (for tutor overview)
    public class LessonAttendanceSummaryDto
    {
        public string LessonId { get; set; } = default!;
        public int LessonNumber { get; set; }
        public DateTime LessonDate { get; set; }
        public DateTime StartTime { get; set; }
        public DateTime EndTime { get; set; }

        public int TotalStudents { get; set; }
        public int PresentCount { get; set; }
        public int LateCount { get; set; }
        public int AbsentCount { get; set; }
        public int ExcusedCount { get; set; }
        public int NotMarkedCount { get; set; }
        public double AttendanceRate { get; set; } // Percentage
    }

    // Tutor overview response - combines both tabs
    public class ClassAttendanceOverviewDto
    {
        public string ClassId { get; set; } = default!;
        public string ClassName { get; set; } = default!;
        public string? Subject { get; set; }
        public int TotalStudents { get; set; }
        public int TotalLessons { get; set; }

        // Tab 1 data: Theo học sinh
        public List<StudentAttendanceSummaryDto> Students { get; set; } = new();

        // Tab 2 data: Theo buổi học
        public List<LessonAttendanceSummaryDto> Lessons { get; set; } = new();
    }

    // Drill down student (from Tab 1 click)
    public class StudentAttendanceDetailDto
    {
        public string StudentId { get; set; } = default!;
        public string StudentUserId { get; set; } = default!;
        public string StudentName { get; set; } = default!;
        public string? Email { get; set; }
        public string? AvatarUrl { get; set; }
        public string ClassId { get; set; } = default!;
        public string ClassName { get; set; } = default!;

        // Summary stats
        public int TotalLessons { get; set; }
        public int PresentCount { get; set; }
        public int LateCount { get; set; }
        public int AbsentCount { get; set; }
        public int ExcusedCount { get; set; }
        public double AttendanceRate { get; set; }

        // Chi tiết từng buổi
        public List<LessonAttendanceRecordDto> Lessons { get; set; } = new();
    }

    // Individual lesson record for student detail view
    public class LessonAttendanceRecordDto
    {
        public string LessonId { get; set; } = default!;
        public int LessonNumber { get; set; }
        public DateTime LessonDate { get; set; }
        public DateTime StartTime { get; set; }
        public DateTime EndTime { get; set; }
        public string? Status { get; set; } // "Present", "Late", "Absent", "Excused", null if not marked
        public string? Notes { get; set; }
    }

    // Drill down lesson (from Tab 2 click "Xem")
    public class LessonAttendanceDetailDto
    {
        public string LessonId { get; set; } = default!;
        public int LessonNumber { get; set; }
        public DateTime LessonDate { get; set; }
        public DateTime StartTime { get; set; }
        public DateTime EndTime { get; set; }
        public string ClassId { get; set; } = default!;
        public string ClassName { get; set; } = default!;

        // Summary stats
        public int TotalStudents { get; set; }
        public int PresentCount { get; set; }
        public int LateCount { get; set; }
        public int AbsentCount { get; set; }
        public int ExcusedCount { get; set; }
        public double AttendanceRate { get; set; }

        // Chi tiết từng student trong buổi này
        public List<StudentAttendanceInLessonDto> Students { get; set; } = new();
    }

    // Individual student record for lesson detail view
    public class StudentAttendanceInLessonDto
    {
        public string StudentId { get; set; } = default!;
        public string StudentUserId { get; set; } = default!;
        public string StudentName { get; set; } = default!;
        public string? Email { get; set; }
        public string? AvatarUrl { get; set; }
        public string? Status { get; set; } // "Present", "Late", "Absent", "Excused", null if not marked
        public string? Notes { get; set; }
    }
}
