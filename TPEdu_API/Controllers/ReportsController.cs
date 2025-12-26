using BusinessLayer.DTOs.API;
using BusinessLayer.DTOs.Reports;
using BusinessLayer.Helper;
using BusinessLayer.Reports;
using BusinessLayer.Service.Interface;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace TPEdu_API.Controllers
{
    [ApiController]
    [Route("tpedu/v1/reports")]
    public class ReportsController : ControllerBase
    {
        private readonly IReportService _svc;
        private readonly IAutoReportService _autoReportService;

        public ReportsController(IReportService svc, IAutoReportService autoReportService)
        {
            _svc = svc;
            _autoReportService = autoReportService;
        }

        // Student tạo report tới Tutor
        [HttpPost("lessons/{lessonId}/materials/{mediaId}/report")]
        [Authorize(Roles = "Student")]
        public async Task<IActionResult> CreateToTutor(string lessonId, string mediaId, [FromBody] ReportCreateRequest req)
        {
            try
            {
                var uid = User.RequireUserId();
                var id = await _svc.CreateToTutorAsync(uid, lessonId, mediaId, req.Reason);
                return CreatedAtAction(nameof(GetDetailForTutor), new { id }, new { id });
            }
            catch (UnauthorizedAccessException ex)
            {
                return StatusCode(403, ApiResponse<object>.Fail(ex.Message));
            }
            catch (KeyNotFoundException ex)
            {
                return NotFound(ApiResponse<object>.Fail(ex.Message));
            }
            catch (InvalidOperationException ex)
            {
                return BadRequest(ApiResponse<object>.Fail(ex.Message));
            }
            catch (Exception ex)
            {
                return StatusCode(500, ApiResponse<object>.Fail($"Lỗi hệ thống: {ex.Message}"));
            }
        }

        // Student tạo report tới Admin
        [HttpPost("lessons/{lessonId}/materials/{mediaId}/report-to-admin")]
        [Authorize(Roles = "Student")]
        public async Task<IActionResult> CreateToAdmin(string lessonId, string mediaId, [FromBody] ReportCreateRequest req)
        {
            try
            {
                var uid = User.RequireUserId();
                var id = await _svc.CreateToAdminAsync(uid, lessonId, mediaId, req.Reason);
                return CreatedAtAction(nameof(GetDetailForAdmin), new { id }, new { id });
            }
            catch (UnauthorizedAccessException ex)
            {
                return StatusCode(403, ApiResponse<object>.Fail(ex.Message));
            }
            catch (KeyNotFoundException ex)
            {
                return NotFound(ApiResponse<object>.Fail(ex.Message));
            }
            catch (InvalidOperationException ex)
            {
                return BadRequest(ApiResponse<object>.Fail(ex.Message));
            }
            catch (Exception ex)
            {
                return StatusCode(500, ApiResponse<object>.Fail($"Lỗi hệ thống: {ex.Message}"));
            }
        }

        // Report a user (tutor dạy tệ, student gây rối)
        [HttpPost("users/{targetUserId}/report")]
        [Authorize]
        public async Task<IActionResult> ReportUser(string targetUserId, [FromBody] ReportCreateRequest req)
        {
            try
            {
                var uid = User.RequireUserId();
                var id = await _svc.ReportUserAsync(uid, targetUserId, req.Reason);
                return CreatedAtAction(nameof(GetDetailForAdmin), new { id }, new { id });
            }
            catch (UnauthorizedAccessException ex)
            {
                return StatusCode(403, ApiResponse<object>.Fail(ex.Message));
            }
            catch (KeyNotFoundException ex)
            {
                return NotFound(ApiResponse<object>.Fail(ex.Message));
            }
            catch (InvalidOperationException ex)
            {
                return BadRequest(ApiResponse<object>.Fail(ex.Message));
            }
            catch (Exception ex)
            {
                return StatusCode(500, ApiResponse<object>.Fail($"Lỗi hệ thống: {ex.Message}"));
            }
        }

        // Report a lesson (buổi học có vấn đề)
        [HttpPost("lessons/{lessonId}/report")]
        [Authorize]
        public async Task<IActionResult> ReportLesson(string lessonId, [FromBody] ReportCreateRequest req)
        {
            try
            {
                var uid = User.RequireUserId();
                var id = await _svc.ReportLessonAsync(uid, lessonId, req.Reason);
                return CreatedAtAction(nameof(GetDetailForAdmin), new { id }, new { id });
            }
            catch (UnauthorizedAccessException ex)
            {
                return StatusCode(403, ApiResponse<object>.Fail(ex.Message));
            }
            catch (KeyNotFoundException ex)
            {
                return NotFound(ApiResponse<object>.Fail(ex.Message));
            }
            catch (InvalidOperationException ex)
            {
                return BadRequest(ApiResponse<object>.Fail(ex.Message));
            }
            catch (Exception ex)
            {
                return StatusCode(500, ApiResponse<object>.Fail($"Lỗi hệ thống: {ex.Message}"));
            }
        }

        // Tutor – list report gửi cho mình
        [HttpGet("tutor")]
        [Authorize(Roles = "Tutor")]
        public async Task<IActionResult> GetForTutor([FromQuery] ReportQuery q)
        {
            try
            {
                var uid = User.RequireUserId();
                var (items, total) = await _svc.GetForTutorAsync(uid, q);
                return Ok(ApiResponse<object>.Ok(new { total, items }, "Lấy danh sách report (tutor) thành công"));
            }
            catch (Exception ex)
            {
                return StatusCode(500, ApiResponse<object>.Fail($"Lỗi hệ thống: {ex.Message}"));
            }
        }

        // Admin – list report gửi cho admin
        [HttpGet("admin")]
        [Authorize(Roles = "Admin")]
        public async Task<IActionResult> GetForAdmin([FromQuery] ReportQuery q)
        {
            try
            {
                var (items, total) = await _svc.GetForAdminAsync(q);
                return Ok(ApiResponse<object>.Ok(new { total, items }, "Lấy danh sách report (admin) thành công"));
            }
            catch (Exception ex)
            {
                return StatusCode(500, ApiResponse<object>.Fail($"Lỗi hệ thống: {ex.Message}"));
            }
        }

        // Tutor xem detail report của mình
        [HttpGet("tutor/{id}")]
        [Authorize(Roles = "Tutor")]
        public async Task<IActionResult> GetDetailForTutor(string id)
        {
            try
            {
                var uid = User.RequireUserId();
                var dto = await _svc.GetDetailAsync(uid, id, isAdmin: false);
                return Ok(ApiResponse<object>.Ok(dto, "Lấy chi tiết report thành công"));
            }
            catch (UnauthorizedAccessException ex)
            {
                return StatusCode(403, ApiResponse<object>.Fail(ex.Message));
            }
            catch (KeyNotFoundException ex)
            {
                return NotFound(ApiResponse<object>.Fail(ex.Message));
            }
            catch (Exception ex)
            {
                return StatusCode(500, ApiResponse<object>.Fail($"Lỗi hệ thống: {ex.Message}"));
            }
        }

        // Admin xem detail
        [HttpGet("admin/{id}")]
        [Authorize(Roles = "Admin")]
        public async Task<IActionResult> GetDetailForAdmin(string id)
        {
            try
            {
                var uid = User.RequireUserId(); // không dùng, nhưng giữ consistency
                var dto = await _svc.GetDetailAsync(uid, id, isAdmin: true);
                return Ok(ApiResponse<object>.Ok(dto, "Lấy chi tiết report thành công"));
            }
            catch (KeyNotFoundException ex)
            {
                return NotFound(ApiResponse<object>.Fail(ex.Message));
            }
            catch (Exception ex)
            {
                return StatusCode(500, ApiResponse<object>.Fail($"Lỗi hệ thống: {ex.Message}"));
            }
        }

        // Tutor cập nhật trạng thái report của mình
        [HttpPatch("tutor/{id}/status")]
        [Authorize(Roles = "Tutor")]
        public async Task<IActionResult> UpdateStatusForTutor(string id, [FromBody] ReportUpdateStatusRequest req)
        {
            try
            {
                var uid = User.RequireUserId();
                var ok = await _svc.UpdateStatusAsync(uid, id, req.Status, isAdmin: false);
                return Ok(ApiResponse<object>.Ok(new { }, "Cập nhật trạng thái thành công"));
            }
            catch (UnauthorizedAccessException ex)
            {
                return StatusCode(403, ApiResponse<object>.Fail(ex.Message));
            }
            catch (KeyNotFoundException ex)
            {
                return NotFound(ApiResponse<object>.Fail(ex.Message));
            }
            catch (Exception ex)
            {
                return StatusCode(500, ApiResponse<object>.Fail($"Lỗi hệ thống: {ex.Message}"));
            }
        }

        // Admin cập nhật trạng thái report gửi admin
        [HttpPatch("admin/{id}/status")]
        [Authorize(Roles = "Admin")]
        public async Task<IActionResult> UpdateStatusForAdmin(string id, [FromBody] ReportUpdateStatusRequest req)
        {
            try
            {
                var uid = User.RequireUserId();
                var ok = await _svc.UpdateStatusAsync(uid, id, req.Status, isAdmin: true);
                return Ok(ApiResponse<object>.Ok(new { }, "Cập nhật trạng thái thành công"));
            }
            catch (KeyNotFoundException ex)
            {
                return NotFound(ApiResponse<object>.Fail(ex.Message));
            }
            catch (Exception ex)
            {
                return StatusCode(500, ApiResponse<object>.Fail($"Lỗi hệ thống: {ex.Message}"));
            }
        }

        // Tutor hủy report (soft delete)
        [HttpDelete("tutor/{id}")]
        [Authorize(Roles = "Tutor")]
        public async Task<IActionResult> CancelReportForTutor(string id)
        {
            try
            {
                var uid = User.RequireUserId();
                var ok = await _svc.CancelReportAsync(uid, id, isAdmin: false);
                return Ok(ApiResponse<object>.Ok(new { }, "Hủy report thành công"));
            }
            catch (UnauthorizedAccessException ex)
            {
                return StatusCode(403, ApiResponse<object>.Fail(ex.Message));
            }
            catch (KeyNotFoundException ex)
            {
                return NotFound(ApiResponse<object>.Fail(ex.Message));
            }
            catch (Exception ex)
            {
                return StatusCode(500, ApiResponse<object>.Fail($"Lỗi hệ thống: {ex.Message}"));
            }
        }

        // Admin hủy report (soft delete)
        [HttpDelete("admin/{id}")]
        [Authorize(Roles = "Admin")]
        public async Task<IActionResult> CancelReportForAdmin(string id)
        {
            try
            {
                var uid = User.RequireUserId();
                var ok = await _svc.CancelReportAsync(uid, id, isAdmin: true);
                return Ok(ApiResponse<object>.Ok(new { }, "Hủy report thành công"));
            }
            catch (KeyNotFoundException ex)
            {
                return NotFound(ApiResponse<object>.Fail(ex.Message));
            }
            catch (Exception ex)
            {
                return StatusCode(500, ApiResponse<object>.Fail($"Lỗi hệ thống: {ex.Message}"));
            }
        }

        [HttpPost("student-response")]
        [AllowAnonymous]
        public async Task<IActionResult> RecordStudentResponse([FromBody] StudentResponseRequest req)
        {
            try
            {
                var result = await _svc.RecordStudentResponseAsync(req.Token, req.Action);
                return result.Status == "Success" ? Ok(result) : BadRequest(result);
            }
            catch (Exception ex)
            {
                return StatusCode(500, ApiResponse<bool>.Fail($"Lỗi: {ex.Message}"));
            }
        }

        /// <summary>
        /// [ADMIN] Get paginated list of auto-reports
        /// </summary>
        [HttpGet("auto-reports")]
        [Authorize(Roles = "Admin")]
        public async Task<IActionResult> GetAutoReports([FromQuery] AutoReportQuery query)
        {
            try
            {
                var result = await _autoReportService.GetAutoReportsAsync(query);
                return Ok(ApiResponse<AutoReportPagedResponse>.Ok(result));
            }
            catch (Exception ex)
            {
                return StatusCode(500, ApiResponse<object>.Fail($"Lỗi hệ thống: {ex.Message}"));
            }
        }
    }
}
