using BusinessLayer.DTOs.API;
using BusinessLayer.DTOs.Feedback;
using BusinessLayer.Helper;
using BusinessLayer.Service.Interface;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace TPEdu_API.Controllers
{
    [ApiController]
    [Route("tpedu/v1/feedbacks")]
    public class FeedbacksController : ControllerBase
    {
        private readonly IFeedbackService _service;
        public FeedbacksController(IFeedbackService service) => _service = service;

        // (1) Tạo feedback cho BUỔI HỌC (lesson-only)
        [HttpPost]
        [Authorize] // Tutor/Student/Parent
        public async Task<IActionResult> Create([FromBody] CreateFeedbackRequest req)
        {
            if (!ModelState.IsValid)
                return BadRequest(ApiResponse<FeedbackDto>.Fail("Dữ liệu không hợp lệ"));

            try
            {
                var actorUserId = User.RequireUserId();
                var dto = await _service.CreateAsync(actorUserId, req);
                return Ok(ApiResponse<FeedbackDto>.Ok(dto, "Tạo feedback thành công"));
            }
            catch (Exception ex)
            {
                return BadRequest(ApiResponse<FeedbackDto>.Fail(ex.Message));
            }
        }

        // (2) Tạo feedback CÔNG KHAI trên TRANG TUTOR (không gắn lesson)
        [HttpPost("tutors/{tutorUserId}")]
        [Authorize]
        public async Task<IActionResult> CreateForTutorProfile(string tutorUserId, [FromBody] CreateTutorProfileFeedbackRequest req)
        {
            if (!ModelState.IsValid)
                return BadRequest(ApiResponse<FeedbackDto>.Fail("Dữ liệu không hợp lệ"));

            try
            {
                var actorUserId = User.RequireUserId();
                var dto = await _service.CreateForTutorProfileAsync(actorUserId, tutorUserId, req);
                return Ok(ApiResponse<FeedbackDto>.Ok(dto, "Tạo feedback công khai thành công"));
            }
            catch (Exception ex)
            {
                return BadRequest(ApiResponse<FeedbackDto>.Fail(ex.Message));
            }
        }

        [HttpPut("{feedbackId}")]
        [Authorize]
        public async Task<IActionResult> Update(string feedbackId, [FromBody] UpdateFeedbackRequest req)
        {
            if (!ModelState.IsValid)
                return BadRequest(ApiResponse<FeedbackDto>.Fail("Dữ liệu không hợp lệ"));

            try
            {
                var actorUserId = User.RequireUserId();
                var dto = await _service.UpdateAsync(actorUserId, feedbackId, req);
                return Ok(ApiResponse<FeedbackDto>.Ok(dto, "Cập nhật feedback thành công"));
            }
            catch (Exception ex)
            {
                return BadRequest(ApiResponse<FeedbackDto>.Fail(ex.Message));
            }
        }

        // Update/Delete giữ nguyên…

        // (3) List theo BUỔI HỌC (hiển thị lesson-only)
        [HttpGet("lesson/{lessonId}")]
        [Authorize]
        public async Task<IActionResult> GetByLesson(string lessonId)
        {
            try
            {
                var items = await _service.GetLessonFeedbacksAsync(lessonId);
                return Ok(ApiResponse<IEnumerable<FeedbackDto>>.Ok(items));
            }
            catch (Exception ex)
            {
                return BadRequest(ApiResponse<IEnumerable<FeedbackDto>>.Fail(ex.Message));
            }
        }

        // (4) List trên TRANG TUTOR (chỉ các bài public)
        [HttpGet("tutors/{tutorUserId}")]
        [AllowAnonymous]
        [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status200OK)]
        public async Task<IActionResult> GetByTutor(string tutorUserId, [FromQuery] int page = 1, [FromQuery] int pageSize = 10)
        {
            try
            {
                var (items, total) = await _service.GetTutorFeedbacksAsync(tutorUserId, page, pageSize);
                var totalPages = pageSize <= 0 ? 0 : (int)Math.Ceiling((double)total / pageSize);

                var data = new
                {
                    items,
                    paging = new
                    {
                        page,
                        pageSize,
                        total,
                        totalPages
                    }
                };

                return Ok(ApiResponse<object>.Ok(data));
            }
            catch (Exception ex)
            {
                return BadRequest(ApiResponse<object>.Fail(ex.Message));
            }
        }

        // (5) Rating tổng hợp cho TUTOR
        [HttpGet("tutors/{tutorUserId}/rating")]
        [AllowAnonymous]
        public async Task<IActionResult> GetTutorRating(string tutorUserId)
        {
            try
            {
                var sum = await _service.GetTutorRatingAsync(tutorUserId);
                return Ok(ApiResponse<TutorRatingSummaryDto>.Ok(sum));
            }
            catch (Exception ex)
            {
                return BadRequest(ApiResponse<TutorRatingSummaryDto>.Fail(ex.Message));
            }
        }

        // (6) Xoá feedback
        [HttpDelete("{feedbackId}")]
        [Authorize]
        public async Task<IActionResult> Delete(string feedbackId)
        {
            try
            {
                var actorUserId = User.RequireUserId();
                var ok = await _service.DeleteAsync(actorUserId, feedbackId);
                if (ok) return Ok(ApiResponse<object>.Ok(new { success = true }, "Đã xoá feedback"));
                return BadRequest(ApiResponse<object>.Fail("Xoá feedback thất bại"));
            }
            catch (Exception ex)
            {
                return BadRequest(ApiResponse<object>.Fail(ex.Message));
            }
        }
    }
}
