using BusinessLayer.DTOs.API;
using BusinessLayer.DTOs.Attendance;
using BusinessLayer.Service.Interface;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using TPEdu_API.Common.Extensions;

namespace TPEdu_API.Controllers
{
    [ApiController]
    [Route("tpedu/v1/attendance")]
    [Authorize]
    public class AttendanceController : ControllerBase
    {
        private readonly IAttendanceService _svc;
        public AttendanceController(IAttendanceService svc) => _svc = svc;

        // Tutor mark single
        [HttpPost("mark")]
        [Authorize(Roles = "Tutor")]
        public async Task<IActionResult> Mark([FromBody] MarkAttendanceRequest req)
        {
            try
            {
                var tutorId = User.RequireUserId();
                var dto = await _svc.MarkAsync(tutorId, req);
                return Ok(ApiResponse<object>.Ok(dto, "điểm danh thành công"));
            }
            catch (UnauthorizedAccessException ex)
            {
                return StatusCode(403, ApiResponse<object>.Fail(ex.Message));
            }
            catch (KeyNotFoundException ex)
            {
                return NotFound(ApiResponse<object>.Fail(ex.Message));
            }
            catch (ArgumentException ex)
            {
                return BadRequest(ApiResponse<object>.Fail(ex.Message));
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

        // Tutor mark bulk
        [HttpPost("mark-bulk/{lessonId}")]
        [Authorize(Roles = "Tutor")]
        public async Task<IActionResult> MarkBulk(string lessonId, [FromBody] BulkMarkRequest body)
        {
            try
            {
                var tutorId = User.RequireUserId();
                var rs = await _svc.BulkMarkForLessonAsync(tutorId, lessonId, body.StudentStatus, body.Notes);
                return Ok(ApiResponse<object>.Ok(rs, "điểm danh hàng loạt thành công"));
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

        // Tutor xem danh sách 1 buổi
        [HttpGet("lesson/{lessonId}")]
        [Authorize(Roles = "Tutor")]
        public async Task<IActionResult> GetLessonAttendance(string lessonId)
        {
            var tutorId = User.RequireUserId();
            var list = await _svc.GetLessonAttendanceAsync(tutorId, lessonId);
            return Ok(ApiResponse<object>.Ok(list, "lấy danh sách thành công"));
        }

        // Calendar overlay - Tutor
        [HttpGet("calendar/tutor")]
        [Authorize(Roles = "Tutor")]
        public async Task<IActionResult> TutorCalendar([FromQuery] DateTime start, [FromQuery] DateTime end)
        {
            var tutorId = User.RequireUserId();
            var data = await _svc.GetTutorAttendanceOnCalendarAsync(tutorId, start, end);
            return Ok(ApiResponse<object>.Ok(data, "ok"));
        }

        // Calendar overlay - Student
        [HttpGet("calendar/student")]
        [Authorize(Roles = "Student")]
        public async Task<IActionResult> StudentCalendar([FromQuery] DateTime start, [FromQuery] DateTime end)
        {
            var studentUserId = User.RequireUserId();
            var data = await _svc.GetStudentAttendanceOnCalendarAsync(studentUserId, start, end);
            return Ok(ApiResponse<object>.Ok(data, "ok"));
        }

        // Calendar overlay - Parent xem con
        [HttpGet("calendar/child/{studentId}")]
        [Authorize(Roles = "Parent")]
        public async Task<IActionResult> ParentChildCalendar(string studentId, [FromQuery] DateTime start, [FromQuery] DateTime end)
        {
            var parentUserId = User.RequireUserId();
            var data = await _svc.GetParentChildAttendanceCalendarAsync(parentUserId, studentId, start, end);
            return Ok(ApiResponse<object>.Ok(data, "ok"));
        }

        [HttpGet("lesson/{lessonId}/roster")]
        [Authorize(Roles = "Tutor")]
        public async Task<IActionResult> GetLessonRoster(string lessonId)
        {
            var tutorId = User.RequireUserId();
            var data = await _svc.GetLessonRosterForTutorAsync(tutorId, lessonId);
            return Ok(ApiResponse<object>.Ok(data, "lấy danh sách học sinh của buổi học thành công"));
        }

        // ========== NEW: Class-Based Attendance Endpoints ==========

        // Tutor: Get overview của class (both tabs - students & lessons)
        [HttpGet("class/{classId}/overview")]
        [Authorize(Roles = "Tutor")]
        public async Task<IActionResult> GetClassAttendanceOverview(string classId)
        {
            try
            {
                var tutorId = User.RequireUserId();
                var data = await _svc.GetClassAttendanceOverviewAsync(tutorId, classId);
                return Ok(ApiResponse<object>.Ok(data, "lấy tổng quan điểm danh lớp thành công"));
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

        // Tutor: Drill down vào 1 student trong class
        [HttpGet("class/{classId}/student/{studentId}")]
        [Authorize(Roles = "Tutor")]
        public async Task<IActionResult> GetStudentAttendanceInClass(string classId, string studentId)
        {
            try
            {
                var tutorId = User.RequireUserId();
                var data = await _svc.GetStudentAttendanceInClassAsync(tutorId, classId, studentId);
                return Ok(ApiResponse<object>.Ok(data, "lấy chi tiết điểm danh học sinh thành công"));
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

        // Tutor: Drill down vào 1 lesson (xem tất cả students trong buổi đó)
        [HttpGet("lesson/{lessonId}/detail")]
        [Authorize(Roles = "Tutor")]
        public async Task<IActionResult> GetLessonAttendanceDetail(string lessonId)
        {
            try
            {
                var tutorId = User.RequireUserId();
                var data = await _svc.GetLessonAttendanceDetailAsync(tutorId, lessonId);
                return Ok(ApiResponse<object>.Ok(data, "lấy chi tiết điểm danh buổi học thành công"));
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

        // Student: Xem attendance của mình trong một class
        [HttpGet("class/{classId}/my-attendance")]
        [Authorize(Roles = "Student")]
        public async Task<IActionResult> GetMyClassAttendance(string classId)
        {
            try
            {
                var studentUserId = User.RequireUserId();
                var data = await _svc.GetMyClassAttendanceAsync(studentUserId, classId);
                return Ok(ApiResponse<object>.Ok(data, "lấy điểm danh của bạn thành công"));
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

        // Parent: Xem attendance của con trong một class
        [HttpGet("class/{classId}/child/{studentId}")]
        [Authorize(Roles = "Parent")]
        public async Task<IActionResult> GetChildClassAttendance(string classId, string studentId)
        {
            try
            {
                var parentUserId = User.RequireUserId();
                var data = await _svc.GetChildClassAttendanceAsync(parentUserId, studentId, classId);
                return Ok(ApiResponse<object>.Ok(data, "lấy điểm danh của con bạn thành công"));
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
    }
}
