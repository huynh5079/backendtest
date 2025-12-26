using BusinessLayer.DTOs.API;
using BusinessLayer.DTOs.Profile;
using BusinessLayer.Service.Interface;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using System.Security.Claims;
using TPEdu_API.Common.Extensions;

namespace TPEdu_API.Controllers
{
    [ApiController]
    [Route("tpedu/v1/profile")]
    [Authorize]
    public class ProfileController : ControllerBase
    {
        private readonly IProfileService _svc;

        public ProfileController(IProfileService svc) => _svc = svc;

        [HttpGet("student")]
        [Authorize(Roles = "Student")]
        public async Task<IActionResult> GetStudent()
        {
            var uid = User.RequireUserId();
            var dto = await _svc.GetStudentProfileAsync(uid);
            return Ok(ApiResponse<object>.Ok(dto, "xem hồ sơ học sinh thành công"));
        }

        [HttpGet("parent")]
        [Authorize(Roles = "Parent")]
        public async Task<IActionResult> GetParent()
        {
            var uid = User.RequireUserId();
            var dto = await _svc.GetParentProfileAsync(uid);
            return Ok(ApiResponse<object>.Ok(dto, "xem hồ sơ phụ huynh thành công"));
        }

        [HttpGet("tutor")]
        [Authorize(Roles = "Tutor")]
        public async Task<IActionResult> GetTutor()
        {
            var uid = User.RequireUserId();
            var dto = await _svc.GetTutorProfileAsync(uid);
            return Ok(ApiResponse<object>.Ok(dto, "xem hồ sơ gia sư thành công"));
        }

        [HttpPut("update/student")]
        public async Task<IActionResult> UpdateStudent([FromBody] UpdateStudentProfileRequest req, CancellationToken ct)
        {
            if (!ModelState.IsValid)
                throw new ArgumentException("dữ liệu cập nhật không hợp lệ");

            var uid = User.RequireUserId();
            await _svc.UpdateStudentAsync(uid, req, ct);
            return Ok(ApiResponse<object>.Ok(new { }, "cập nhật thành công"));
        }

        [HttpPut("update/parent")]
        public async Task<IActionResult> UpdateParent([FromBody] UpdateParentProfileRequest req, CancellationToken ct)
        {
            if (!ModelState.IsValid)
                throw new ArgumentException("dữ liệu cập nhật không hợp lệ");

            var uid = User.RequireUserId();
            await _svc.UpdateParentAsync(uid, req, ct);
            return Ok(ApiResponse<object>.Ok(new { }, "cập nhật thành công"));
        }

        [HttpPut("update/tutor")]
        public async Task<IActionResult> UpdateTutor([FromBody] UpdateTutorProfileRequest req, CancellationToken ct)
        {
            if (!ModelState.IsValid)
                throw new ArgumentException("dữ liệu cập nhật không hợp lệ");

            var uid = User.RequireUserId();
            await _svc.UpdateTutorAsync(uid, req, ct);
            return Ok(ApiResponse<object>.Ok(new { }, "cập nhật thành công"));
        }

        [HttpPut("avatar")]
        [Consumes("multipart/form-data")]
        [RequestSizeLimit(20_000_000)] // 20MB limit
        public async Task<IActionResult> UpdateAvatar([FromForm] UpdateAvatarRequest req, CancellationToken ct)
        {
            if (req.Avatar == null)
                throw new ArgumentException("File avatar là bắt buộc.");

            var uid = User.RequireUserId();
            var newAvatarUrl = await _svc.UpdateAvatarAsync(uid, req.Avatar, ct);
            return Ok(ApiResponse<object>.Ok(new { avatarUrl = newAvatarUrl }, "Cập nhật avatar thành công"));
        }

        // ========== CERTIFICATE MANAGEMENT ==========

        [HttpPost("tutor/certificates")]
        [Authorize(Roles = "Tutor")]
        [Consumes("multipart/form-data")]
        [RequestSizeLimit(100_000_000)] // 100MB total limit
        public async Task<IActionResult> UploadCertificates([FromForm] UploadCertificatesRequest req, CancellationToken ct)
        {
            if (!ModelState.IsValid)
                throw new ArgumentException("Dữ liệu không hợp lệ");

            var userId = User.RequireUserId();
            var mediaList = await _svc.UploadTutorCertificatesAsync(userId, req.Certificates, ct);

            return Ok(ApiResponse<object>.Ok(
                new { certificates = mediaList },
                "Upload chứng chỉ thành công"));
        }

        [HttpDelete("tutor/certificates/{mediaId}")]
        [Authorize(Roles = "Tutor")]
        public async Task<IActionResult> DeleteCertificate(string mediaId, CancellationToken ct)
        {
            var userId = User.RequireUserId();
            await _svc.DeleteTutorCertificateAsync(userId, mediaId, ct);

            return Ok(ApiResponse<object>.Ok(new { }, "Xóa chứng chỉ thành công"));
        }

        // ========== IDENTITY DOCUMENT MANAGEMENT ==========

        [HttpPost("tutor/identity-documents")]
        [Authorize(Roles = "Tutor")]
        [Consumes("multipart/form-data")]
        [RequestSizeLimit(50_000_000)] // 50MB total limit
        public async Task<IActionResult> UploadIdentityDocuments([FromForm] UploadIdentityDocumentsRequest req, CancellationToken ct)
        {
            if (!ModelState.IsValid)
                throw new ArgumentException("Dữ liệu không hợp lệ");

            var userId = User.RequireUserId();
            var mediaList = await _svc.UploadTutorIdentityDocumentsAsync(userId, req.IdentityDocuments, ct);

            return Ok(ApiResponse<object>.Ok(
                new { identityDocuments = mediaList },
                "Upload giấy tờ tùy thân thành công"));
        }

        [HttpDelete("tutor/identity-documents/{mediaId}")]
        [Authorize(Roles = "Tutor")]
        public async Task<IActionResult> DeleteIdentityDocument(string mediaId, CancellationToken ct)
        {
            var userId = User.RequireUserId();
            await _svc.DeleteTutorIdentityDocumentAsync(userId, mediaId, ct);

            return Ok(ApiResponse<object>.Ok(new { }, "Xóa giấy tờ tùy thân thành công"));
        }
    }
}
