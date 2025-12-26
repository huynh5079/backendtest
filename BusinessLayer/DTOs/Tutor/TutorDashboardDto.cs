using System;
using System.Collections.Generic;

namespace BusinessLayer.DTOs.Tutor
{
    /// <summary>
    /// DTO chứa thông tin thống kê cho Tutor Dashboard
    /// </summary>
    public class TutorDashboardDto
    {
        // ===== Part 1: Basic Statistics =====
        
        /// <summary>
        /// Tổng số lớp đang dạy (Status = InProgress)
        /// </summary>
        public int TotalActiveClasses { get; set; }
        
        /// <summary>
        /// Tổng số học viên trong tất cả các lớp đang dạy
        /// </summary>
        public int TotalStudents { get; set; }
        
        /// <summary>
        /// Tổng thu nhập tháng này (VNĐ)
        /// </summary>
        public decimal MonthlyIncome { get; set; }

        // ===== Part 2: Lesson Statistics =====
        
        /// <summary>
        /// Số buổi dạy trong tuần này
        /// </summary>
        public int LessonsThisWeek { get; set; }
        
        /// <summary>
        /// Số buổi dạy trong tháng này
        /// </summary>
        public int LessonsThisMonth { get; set; }

        // ===== Part 3: Student Statistics =====
        
        /// <summary>
        /// Tổng số học viên đang học (ClassAssign approved trong các lớp active)
        /// </summary>
        public int ActiveStudents { get; set; }
        
        /// <summary>
        /// Số học viên mới đăng ký trong tháng này
        /// </summary>
        public int NewStudentsThisMonth { get; set; }
    }

    /// <summary>
    /// Thống kê thu nhập theo tháng
    /// </summary>
    public class MonthlyStatDto
    {
        public int Month { get; set; }
        public decimal Amount { get; set; }
    }

    /// <summary>
    /// Thống kê thu nhập trong năm (12 tháng)
    /// </summary>
    public class YearlyIncomeDto
    {
        public int Year { get; set; }
        public decimal TotalIncome { get; set; }
        public List<MonthlyStatDto> MonthlyData { get; set; } = new();
    }

    /// <summary>
    /// Thống kê buổi học theo tháng
    /// </summary>
    public class MonthlyLessonStatDto
    {
        public int Month { get; set; }
        public int LessonCount { get; set; }
    }

    /// <summary>
    /// Thống kê buổi học trong năm (12 tháng)
    /// </summary>
    public class YearlyLessonsDto
    {
        public int Year { get; set; }
        public int TotalLessons { get; set; }
        public List<MonthlyLessonStatDto> MonthlyData { get; set; } = new();
    }
}

