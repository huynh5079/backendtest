using BusinessLayer.DTOs.API;
using BusinessLayer.DTOs.Schedule.Class;
using BusinessLayer.DTOs.Schedule.ClassAssign;
using BusinessLayer.Service.Interface.IScheduleService;
using DataLayer.Enum;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using TPEdu_API.Common.Extensions;

namespace TPEdu_API.Controllers
{
    [ApiController]
    [Route("tpedu/v1/admin/classes")]
    [Authorize(Roles = "Admin")]
    public class AdminClassController : ControllerBase
    {
        private readonly IClassService _classService;
        private readonly IAssignService _assignService;

        public AdminClassController(IClassService classService, IAssignService assignService)
        {
            _classService = classService;
            _assignService = assignService;
        }

        /// <summary>
        /// Admin lấy danh sách tất cả lớp học
        /// </summary>
        [HttpGet]
        public async Task<IActionResult> GetAllClasses([FromQuery] ClassStatus? status = null)
        {
            try
            {
                var classes = await _classService.GetAllClassesForAdminAsync(status);
                var classesList = classes.ToList();
                // Log để debug
                Console.WriteLine($"[AdminClassController] GetAllClasses - Status filter: {status}, Count: {classesList.Count}");
                return Ok(ApiResponse<IEnumerable<ClassDto>>.Ok(classesList, "Lấy danh sách lớp học thành công."));
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[AdminClassController] GetAllClasses - Error: {ex.Message}");
                Console.WriteLine($"[AdminClassController] GetAllClasses - StackTrace: {ex.StackTrace}");
                return StatusCode(500, ApiResponse<object>.Fail($"Lỗi hệ thống: {ex.Message}"));
            }
        }

        /// <summary>
        /// Admin đồng bộ lại CurrentStudentCount cho một lớp
        /// Dùng để sửa các lớp đã bị lệch CurrentStudentCount
        /// </summary>
        [HttpPost("{classId}/sync-student-count")]
        public async Task<IActionResult> SyncStudentCount(string classId)
        {
            try
            {
                var result = await _classService.SyncCurrentStudentCountAsync(classId);
                return Ok(ApiResponse<bool>.Ok(result, "Đồng bộ số học sinh thành công."));
            }
            catch (Exception ex)
            {
                return StatusCode(500, ApiResponse<object>.Fail($"Lỗi hệ thống: {ex.Message}"));
            }
        }

        /// <summary>
        /// Admin lấy chi tiết lớp học
        /// </summary>
        [HttpGet("{classId}/detail")]
        public async Task<IActionResult> GetClassDetail(string classId)
        {
            try
            {
                var classDetail = await _classService.GetClassByIdAsync(classId);
                if (classDetail == null)
                    return NotFound(ApiResponse<object>.Fail("Không tìm thấy lớp học."));
                
                return Ok(ApiResponse<ClassDto>.Ok(classDetail, "Lấy chi tiết lớp học thành công"));
            }
            catch (Exception ex)
            {
                return StatusCode(500, ApiResponse<object>.Fail($"Lỗi hệ thống: {ex.Message}"));
            }
        }

        /// <summary>
        /// Admin lấy danh sách học viên của lớp học
        /// </summary>
        [HttpGet("{classId}/students")]
        public async Task<IActionResult> GetStudentsInClass(string classId)
        {
            try
            {
                var students = await _assignService.GetStudentsInClassForAdminAsync(classId);
                return Ok(ApiResponse<List<StudentEnrollmentDto>>.Ok(students, "Lấy danh sách học viên thành công."));
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

        /// <summary>
        /// Admin hủy lớp học (toàn bộ lớp)
        /// </summary>
        [HttpPost("{classId}/cancel")]
        public async Task<IActionResult> CancelClass(string classId, [FromBody] CancelClassRequestBodyDto requestBody)
        {
            try
            {
                var adminUserId = User.GetUserId();
                if (string.IsNullOrEmpty(adminUserId))
                    return Unauthorized(new { message = "Token không hợp lệ." });

                // Map từ body DTO sang service DTO với ClassId từ route
                var request = new CancelClassRequestDto
                {
                    ClassId = classId,
                    Reason = requestBody.Reason,
                    Note = requestBody.Note,
                    StudentId = requestBody.StudentId
                };

                var result = await _classService.CancelClassByAdminAsync(adminUserId, request);
                
                return Ok(new
                {
                    success = true,
                    message = result.Message,
                    data = new
                    {
                        classId = result.ClassId,
                        newStatus = result.NewStatus.ToString(),
                        reason = result.Reason.ToString(),
                        refundedEscrowsCount = result.RefundedEscrowsCount,
                        totalRefundedAmount = result.TotalRefundedAmount,
                        depositRefunded = result.DepositRefunded,
                        depositRefundAmount = result.DepositRefundAmount
                    }
                });
            }
            catch (KeyNotFoundException ex)
            {
                return NotFound(new { message = ex.Message });
            }
            catch (InvalidOperationException ex)
            {
                return BadRequest(new { message = ex.Message });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { message = "Lỗi hệ thống: " + ex.Message });
            }
        }

        /// <summary>
        /// Admin hủy 1 học sinh khỏi lớp (group class)
        /// </summary>
        [HttpPost("{classId}/students/{studentId}/cancel")]
        public async Task<IActionResult> CancelStudentEnrollment(
            string classId, 
            string studentId, 
            [FromBody] CancelStudentEnrollmentRequestDto request)
        {
            try
            {
                var adminUserId = User.GetUserId();
                if (string.IsNullOrEmpty(adminUserId))
                    return Unauthorized(new { message = "Token không hợp lệ." });

                var result = await _classService.CancelStudentEnrollmentAsync(
                    adminUserId, 
                    classId, 
                    studentId, 
                    request.Reason, 
                    request.Note);
                
                return Ok(new
                {
                    success = true,
                    message = result.Message,
                    data = new
                    {
                        classId = result.ClassId,
                        newStatus = result.NewStatus.ToString(),
                        reason = result.Reason.ToString(),
                        refundedEscrowsCount = result.RefundedEscrowsCount,
                        totalRefundedAmount = result.TotalRefundedAmount
                    }
                });
            }
            catch (KeyNotFoundException ex)
            {
                return NotFound(new { message = ex.Message });
            }
            catch (InvalidOperationException ex)
            {
                return BadRequest(new { message = ex.Message });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { message = "Lỗi hệ thống: " + ex.Message });
            }
        }
    }

    /// <summary>
    /// DTO cho cancel student enrollment request
    /// </summary>
    public class CancelStudentEnrollmentRequestDto
    {
        public ClassCancelReason Reason { get; set; }
        public string? Note { get; set; }
    }
}

