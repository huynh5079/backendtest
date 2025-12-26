using BusinessLayer.DTOs.API;
using BusinessLayer.Helper;
using BusinessLayer.Service.Interface;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace TPEdu_API.Controllers
{
    /// <summary>
    /// Controller cho Tutor Dashboard - yêu cầu đăng nhập với role Tutor
    /// </summary>
    [ApiController]
    [Route("tpedu/v1/tutor")]
    [Authorize(Roles = "Tutor")]
    public class TutorDashboardController : ControllerBase
    {
        private readonly ITutorDashboardService _svc;

        public TutorDashboardController(ITutorDashboardService svc)
        {
            _svc = svc;
        }

        /// <summary>
        /// Lấy thống kê tổng quan cho Tutor Dashboard
        /// GET /tpedu/v1/tutor/dashboard
        /// </summary>
        [HttpGet("dashboard")]
        public async Task<IActionResult> GetDashboardStatistics()
        {
            var tutorUserId = User.RequireUserId();
            var stats = await _svc.GetDashboardStatisticsAsync(tutorUserId);
            return Ok(ApiResponse<object>.Ok(stats, "Lấy thống kê dashboard thành công"));
        }

        /// <summary>
        /// Lấy thống kê thu nhập theo từng tháng trong năm
        /// GET /tpedu/v1/tutor/dashboard/income?year=2024
        /// </summary>
        [HttpGet("dashboard/income")]
        public async Task<IActionResult> GetYearlyIncome([FromQuery] int? year = null)
        {
            var tutorUserId = User.RequireUserId();
            var targetYear = year ?? DateTimeHelper.VietnamNow.Year;
            var stats = await _svc.GetYearlyIncomeAsync(tutorUserId, targetYear);
            return Ok(ApiResponse<object>.Ok(stats, "Lấy thống kê thu nhập theo năm thành công"));
        }

        /// <summary>
        /// Lấy thống kê buổi học theo từng tháng trong năm
        /// GET /tpedu/v1/tutor/dashboard/lessons?year=2024
        /// </summary>
        [HttpGet("dashboard/lessons")]
        public async Task<IActionResult> GetYearlyLessons([FromQuery] int? year = null)
        {
            var tutorUserId = User.RequireUserId();
            var targetYear = year ?? DateTimeHelper.VietnamNow.Year;
            var stats = await _svc.GetYearlyLessonsAsync(tutorUserId, targetYear);
            return Ok(ApiResponse<object>.Ok(stats, "Lấy thống kê buổi học theo năm thành công"));
        }
    }
}

