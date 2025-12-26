using BusinessLayer.DTOs.API;
using BusinessLayer.DTOs.Schedule.ClassRequest;
using BusinessLayer.DTOs.Schedule.TutorApplication;
using BusinessLayer.Service.Interface;
using BusinessLayer.Service.Interface.IScheduleService;
using DataLayer.Enum;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.Security.Claims;
using TPEdu_API.Common.Extensions;

namespace TPEdu_API.Controllers
{

    [Route("tpedu/v1/class-request")]
    [ApiController]
    public class ClassRequestController : ControllerBase
    {
        private readonly IClassRequestService _classRequestService;
        private readonly IStudentProfileService _studentProfileService;
        private readonly ITutorProfileService _tutorProfileService;

        public ClassRequestController(
            IClassRequestService classRequestService,
            IStudentProfileService studentProfileService,
            ITutorProfileService tutorProfileService)
        {
            _classRequestService = classRequestService;
            _studentProfileService = studentProfileService;
            _tutorProfileService = tutorProfileService;
        }

        #region Student & Parent Actions
        /// <summary>
        /// [Student] Tạo một request (direct hoặc marketplace).
        /// </summary>
        [HttpPost]
        [Authorize(Roles = "Student,Parent")]
        public async Task<IActionResult> CreateClassRequest([FromBody] CreateClassRequestDto dto)
        {

            try
            {
                var userId = User.RequireUserId(); // Lấy ID từ Token
                var role = User.FindFirst(ClaimTypes.Role)?.Value ?? string.Empty;

                var result = await _classRequestService.CreateClassRequestAsync(userId, role, dto);

                // Trả về 201 Created
                return CreatedAtAction(nameof(GetClassRequestById), new { id = result?.Id }, ApiResponse<ClassRequestResponseDto>.Ok(result));
            }
            catch (ArgumentException ex) // Lỗi thiếu thông tin (vd: Parent không chọn con)
            {
                return BadRequest(ApiResponse<object>.Fail(ex.Message));
            }
            catch (UnauthorizedAccessException ex) // Lỗi không có quyền (không phải con mình)
            {
                return StatusCode(403, ApiResponse<object>.Fail(ex.Message));
            }
            catch (Exception ex)
            {
                return StatusCode(500, ApiResponse<object>.Fail(ex.Message));
            }
        }

        /// <summary>
        /// [Student] Cập nhật thông tin request (khi còn Pending).
        /// </summary>
        [HttpPut("{id}")]
        [Authorize(Roles = "Student,Parent")]
        public async Task<IActionResult> UpdateClassRequest(string id, [FromBody] UpdateClassRequestDto dto)
        {
            try
            {
            var userId = User.RequireUserId();
            var role = User.FindFirst(System.Security.Claims.ClaimTypes.Role)?.Value ?? "Student";

            var result = await _classRequestService.UpdateClassRequestAsync(userId, role, id, dto);
            if (result == null) return NotFound(ApiResponse<object>.Fail("Không tìm thấy yêu cầu."));

            return Ok(ApiResponse<ClassRequestResponseDto>.Ok(result, "Cập nhật thành công."));
            }
            catch (KeyNotFoundException ex)
            {
                return NotFound(ApiResponse<object>.Fail(ex.Message));
            }
            catch (InvalidOperationException ex) // Lỗi logic (vd: status != Pending)
            {
                return BadRequest(ApiResponse<object>.Fail(ex.Message));
            }
            catch (UnauthorizedAccessException ex)
            {
                return StatusCode(403, ApiResponse<object>.Fail(ex.Message));
            }
            catch (Exception ex)
            {
                return StatusCode(500, ApiResponse<object>.Fail(ex.Message));
            }
        }

        /// <summary>
        /// [Student] Cập nhật lịch (schedules) cho một request (khi còn Pending).
        /// </summary>
        [HttpPut("{id}/schedules")]
        [Authorize(Roles = "Student,Parent")]
        public async Task<IActionResult> UpdateClassRequestSchedules(string id, [FromBody] List<ClassRequestScheduleDto> scheduleDtos)
        {

            var userId = User.GetUserId();
            if (userId == null)
                return Unauthorized(new { message = "Token không hợp lệ." });
            var role = User.FindFirst(System.Security.Claims.ClaimTypes.Role)?.Value ?? "Student";

            await _classRequestService.UpdateClassRequestScheduleAsync(userId, role, id, scheduleDtos);
            return Ok(new { message = "Cập nhật lịch thành công." });
        }

        /// <summary>
        /// [Student] Hủy một request (khi còn Pending).
        /// </summary>
        [HttpPatch("{id}/cancel")]
        [Authorize(Roles = "Student,Parent")]
        public async Task<IActionResult> CancelClassRequest(string id)
        {

            var userId = User.GetUserId();
            if (userId == null)
                    return Unauthorized(new { message = "Token không hợp lệ." });
            var role = User.FindFirst(System.Security.Claims.ClaimTypes.Role)?.Value ?? "Student";

            await _classRequestService.CancelClassRequestAsync(userId, role, id);
                return Ok(new { message = "Hủy yêu cầu thành công." });
        }

        /// <summary>
        /// [Student] Lấy các request CỦA TÔI.
        /// </summary>
        [HttpGet("my-requests")]
        [Authorize(Roles = "Student,Parent")]
        public async Task<IActionResult> GetMyClassRequests([FromQuery] string? childId)
        {
            try
            {
            var userId = User.GetUserId();
            if (userId == null)
                return Unauthorized(new { message = "Token không hợp lệ." });
            var role = User.FindFirst(System.Security.Claims.ClaimTypes.Role)?.Value ?? "Student";

            var result = await _classRequestService.GetMyClassRequestsAsync(userId, role, childId);
            return Ok(ApiResponse<IEnumerable<ClassRequestResponseDto>>.Ok(result));

            }
            catch (UnauthorizedAccessException ex)
            {
                return StatusCode(403, ApiResponse<object>.Fail(ex.Message));
            }
            catch (Exception ex)
            {
                return StatusCode(500, ApiResponse<object>.Fail(ex.Message));
            }
        }
        #endregion

        #region Tutor Actions
        /// <summary>
        /// [Tutor] Lấy các request "Direct" (gửi thẳng) đến TÔI.
        /// </summary>
        [HttpGet("direct-requests")]
        [Authorize(Roles = "Tutor")]
        public async Task<IActionResult> GetDirectRequests()
        {
            var tutorUserId = User.GetUserId();
            if (tutorUserId == null)
                return Unauthorized(new { message = "Token không hợp lệ." });

            var result = await _classRequestService.GetDirectRequestsAsync(tutorUserId);
            return Ok(result);
        }

        /// <summary>
        /// [Tutor] Phản hồi (Accept/Reject) một request "Direct".
        /// </summary>
        [HttpPatch("{id}/respond")]
        [Authorize(Roles = "Tutor")]
        public async Task<IActionResult> RespondToDirectRequest(
            string id, 
            [FromQuery] bool accept,
            [FromBody] UpdateStatusDto? dto = null)
        {
            try
            {
                var tutorUserId = User.GetUserId();
                if (tutorUserId == null)
                    return Unauthorized(ApiResponse<object>.Fail("Token không hợp lệ."));

                // Xử lý meetingLink: nếu là empty string thì chuyển thành null
                string? meetingLink = string.IsNullOrWhiteSpace(dto?.MeetingLink) ? null : dto.MeetingLink;

                var result = await _classRequestService.RespondToDirectRequestAsync(
                    tutorUserId,
                    id,
                    accept,
                    meetingLink);

                if (accept && result != null)
                {
                    // 3. Trả về Full DTO cho Frontend (FE cần PaymentRequired để redirect)
                    return Ok(ApiResponse<AcceptRequestResponseDto>.Ok(result, result.Message));
                }
                else
                {
                    // Logic Reject
                    return Ok(ApiResponse<object>.Ok(null, "Đã từ chối yêu cầu."));
                }
            }
            catch (KeyNotFoundException ex)
            {
                return NotFound(ApiResponse<object>.Fail(ex.Message));
            }
            catch (UnauthorizedAccessException ex)
            {
                return StatusCode(StatusCodes.Status403Forbidden, ApiResponse<object>.Fail(ex.Message));
            }
            catch (InvalidOperationException ex)
            {
                return BadRequest(ApiResponse<object>.Fail(ex.Message));
            }
            catch (Exception ex)
            {
                return StatusCode(StatusCodes.Status500InternalServerError, ApiResponse<object>.Fail($"Lỗi hệ thống: {ex.Message}"));
            }
        }
        #endregion

        #region Public Actions
        /// <summary>
        /// [Public] Lấy các request trên "Marketplace" (có phân trang).
        /// </summary>
        [HttpGet("marketplace")]
        [AllowAnonymous]
        public async Task<IActionResult> GetMarketplaceRequests(
            [FromQuery] int page = 1, [FromQuery] int pageSize = 10,
            [FromQuery] string? subject = null, [FromQuery] string? educationLevel = null,
            [FromQuery] string? mode = null, [FromQuery] string? locationContains = null)
        {
            // Chỉ lấy các request "Active"
            var (data, totalCount) = await _classRequestService.GetMarketplaceRequestsAsync(
                page, pageSize, null, subject, educationLevel, mode, locationContains);

            return Ok(new { TotalCount = totalCount, Data = data });
        }

        [HttpGet("marketplace-tutor")]
        [Authorize(Roles = "Tutor")]
        public async Task<IActionResult> GetMarketplaceForTutor(
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 10,
        [FromQuery] string? subject = null,
        [FromQuery] string? educationLevel = null,
        [FromQuery] string? mode = null,
        [FromQuery] string? locationContains = null)
        {
            try
            {
                var userId = User.RequireUserId();

                var (data, totalCount) = await _classRequestService.GetMarketplaceForTutorAsync(
                    userId, page, pageSize, subject, educationLevel, mode, locationContains);

                return Ok(ApiResponse<object>.Ok(new
                {
                    totalCount = totalCount,
                    items = data,
                    page = page,
                    pageSize = pageSize
                }, "Lấy danh sách yêu cầu phù hợp thành công."));
            }
            catch (Exception ex)
            {
                // Trả về lỗi 400 kèm message chi tiết chúng ta vừa viết ở Service
                return BadRequest(ApiResponse<object>.Fail(ex.Message));
            }
        }

        /// <summary>
        /// [Public] Lấy chi tiết một request.
        /// </summary>
        [HttpGet("{id}")]
        [AllowAnonymous]
        public async Task<IActionResult> GetClassRequestById(string id)
        {
            var result = await _classRequestService.GetClassRequestByIdAsync(id);
            if (result == null)
                return NotFound(new { message = "Không tìm thấy yêu cầu." });
            
            // Logic hiển thị địa chỉ:
            // - Nếu request chưa có tutor (marketplace) → hiển thị địa chỉ cho tutor để họ biết trước khi apply
            // - Nếu request đã có tutor (direct request) và status là Pending → ẩn địa chỉ cho tutor đó (chưa thanh toán phí kết nối)
            var role = User.FindFirst(ClaimTypes.Role)?.Value;
            if (role == "Tutor" && result.Status == ClassRequestStatus.Pending && result.TutorId != null)
            {
                // Chỉ ẩn địa chỉ nếu request đã có tutor (direct request) và chưa thanh toán
                result.Location = null; // Ẩn địa chỉ học sinh
            }
            // Nếu request chưa có tutor (marketplace), địa chỉ sẽ được hiển thị để tutor biết trước khi apply
            
            return Ok(result);
        }
        #endregion

        /// <summary>
        /// [Admin] Cập nhật trạng thái của một request.
        /// </summary>
        [HttpPatch("{id}/status")]
        [Authorize(Roles = "Admin")]
        public async Task<IActionResult> UpdateClassRequestStatus(string id, [FromBody] UpdateStatusDto dto)
        {
                await _classRequestService.UpdateClassRequestStatusAsync(id, dto);
                return Ok(new { message = $"Cập nhật trạng thái thành '{dto.Status}' thành công." });
        }
    }
}
