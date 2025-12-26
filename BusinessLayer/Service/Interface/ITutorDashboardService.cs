using BusinessLayer.DTOs.Tutor;
using System.Threading.Tasks;

namespace BusinessLayer.Service.Interface
{
    /// <summary>
    /// Service cho Tutor Dashboard thống kê
    /// </summary>
    public interface ITutorDashboardService
    {
        /// <summary>
        /// Lấy thống kê dashboard cho tutor
        /// </summary>
        Task<TutorDashboardDto> GetDashboardStatisticsAsync(string tutorUserId);

        /// <summary>
        /// Lấy thống kê thu nhập theo từng tháng trong năm
        /// </summary>
        Task<YearlyIncomeDto> GetYearlyIncomeAsync(string tutorUserId, int year);

        /// <summary>
        /// Lấy thống kê buổi học theo từng tháng trong năm
        /// </summary>
        Task<YearlyLessonsDto> GetYearlyLessonsAsync(string tutorUserId, int year);
    }
}
