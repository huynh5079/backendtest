using BusinessLayer.DTOs.Feedback;
using BusinessLayer.Service.Interface;
using DataLayer.Entities;
using DataLayer.Repositories.Abstraction;
using Microsoft.EntityFrameworkCore;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace BusinessLayer.Service
{
    public class FeedbackService : IFeedbackService
    {
        private readonly IUnitOfWork _uow;
        private readonly TpeduContext _ctx;

        public FeedbackService(IUnitOfWork uow, TpeduContext ctx)
        {
            _uow = uow;
            _ctx = ctx;
        }

        public async Task<FeedbackDto> CreateAsync(string actorUserId, CreateFeedbackRequest req)
        {
            if (string.IsNullOrWhiteSpace(req.LessonId) || string.IsNullOrWhiteSpace(req.ToUserId))
                throw new ArgumentException("Thiếu LessonId/ToUserId");
            if (req.Rating is < 0 or > 5)
                throw new ArgumentException("Rating phải trong khoảng 0..5");

            var lesson = await _ctx.Lessons
                .Include(l => l.Class)
                .Include(l => l.ScheduleEntries)
                .FirstOrDefaultAsync(l => l.Id == req.LessonId)
                ?? throw new KeyNotFoundException("Không tìm thấy buổi học");

            /*var tutorUserId = lesson.Class?.TutorId
                              ?? lesson.ScheduleEntries.FirstOrDefault()?.TutorId
                              ?? throw new InvalidOperationException("Buổi học chưa gắn tutor");

            var actor = await _uow.Users.GetByIdAsync(actorUserId)
                        ?? throw new KeyNotFoundException("Không tìm thấy user");

            await EnsureParticipantPermission(actor, req.ToUserId, lesson, tutorUserId);*/

            // ===== PHIÊN BẢN SỬA LẠI (TRONG CreateAsync) =====

            // BƯỚC 1: Lấy ID của HỒ SƠ GIA SƯ (TutorProfile.Id) từ buổi học
            var tutorProfileId = lesson.Class?.TutorId
                               ?? lesson.ScheduleEntries.FirstOrDefault()?.TutorId
                               ?? throw new InvalidOperationException("Buổi học chưa gắn tutor");

            // BƯỚC 2: Từ Profile.Id, tìm User.Id tương ứng
            var tutorProfile = await _uow.TutorProfiles.GetAsync(p => p.Id == tutorProfileId)
                               ?? throw new KeyNotFoundException($"Không tìm thấy TutorProfile (Id={tutorProfileId}) cho lớp này.");

            // Đây mới là ID người dùng của gia sư (User.Id)
            var actualTutorUserId = tutorProfile.UserId;

            // BƯỚC 3: Lấy user đang hành động (giữ nguyên)
            var actor = await _uow.Users.GetByIdAsync(actorUserId)
                                ?? throw new KeyNotFoundException("Không tìm thấy user");

            // BƯỚC 4: So sánh
            // Bây giờ actualTutorUserId (từ CSDL) và req.ToUserId (từ payload)
            // đều là User.Id, chúng sẽ khớp!
            await EnsureParticipantPermission(actor, req.ToUserId, lesson, actualTutorUserId);

            var exists = await _uow.Feedbacks.ExistsAsync(actorUserId, req.ToUserId, req.LessonId);
            if (exists) throw new InvalidOperationException("Bạn đã gửi feedback cho buổi học này.");

            var fb = new Feedback
            {
                FromUserId = actorUserId,
                ToUserId = req.ToUserId,
                LessonId = req.LessonId,
                Rating = req.Rating,
                Comment = string.IsNullOrWhiteSpace(req.Comment) ? null : req.Comment.Trim(),
                IsPublicOnTutorProfile = false, // ✅ lesson-only
                CreatedAt = DateTime.Now,
                UpdatedAt = DateTime.Now
            };

            await _uow.Feedbacks.CreateAsync(fb);
            await _uow.SaveChangesAsync();

            // Re-calc rating nếu ToUser là Tutor
            await RecalculateTutorRatingAsync(req.ToUserId);

            var fromName = actor.UserName ?? "User";
            var toName = (await _uow.Users.GetByIdAsync(req.ToUserId))?.UserName ?? "User";
            return MapDto(fb, fromName, toName);
        }

        public async Task<FeedbackDto> CreateForTutorProfileAsync(string actorUserId, string tutorUserId, CreateTutorProfileFeedbackRequest req)
        {
            if (string.IsNullOrWhiteSpace(tutorUserId))
                throw new ArgumentException("Thiếu TutorUserId");
            if (req.Rating is < 0 or > 5)
                throw new ArgumentException("Rating phải trong khoảng 0..5");

            var actor = await _uow.Users.GetByIdAsync(actorUserId)
                        ?? throw new KeyNotFoundException("Không tìm thấy user");
            var tutor = await _uow.Users.GetByIdAsync(tutorUserId)
                        ?? throw new KeyNotFoundException("Không tìm thấy tutor");
            if (tutor.RoleName != "Tutor")
                throw new InvalidOperationException("TutorUserId không phải tài khoản Tutor");

            // Cho phép nhiều bài viết công khai theo thời gian (LessonId = null)
            var fb = new Feedback
            {
                FromUserId = actorUserId,
                ToUserId = tutorUserId,
                LessonId = null,                                  // ✅ không gắn buổi
                IsPublicOnTutorProfile = true,                    // ✅ render trên trang Tutor
                Rating = req.Rating,                              // sẽ KHÔNG tính vào rating tổng
                Comment = string.IsNullOrWhiteSpace(req.Comment) ? null : req.Comment.Trim(),
                CreatedAt = DateTime.Now,
                UpdatedAt = DateTime.Now
            };

            await _uow.Feedbacks.CreateAsync(fb);
            await _uow.SaveChangesAsync();

            var fromName = actor.UserName ?? "User";
            var toName = tutor.UserName ?? "Tutor";
            return MapDto(fb, fromName, toName);
        }

        public async Task<FeedbackDto> UpdateAsync(string actorUserId, string feedbackId, UpdateFeedbackRequest req)
        {
            var fb = await _uow.Feedbacks.GetByIdAsync(feedbackId)
                     ?? throw new KeyNotFoundException("Không tìm thấy feedback");

            if (fb.FromUserId != actorUserId)
                throw new UnauthorizedAccessException("Chỉ người viết feedback mới được sửa");

            if (req.Rating is < 0 or > 5)
                throw new ArgumentException("Rating phải trong khoảng 0..5");

            if (req.Rating.HasValue) fb.Rating = req.Rating.Value;
            if (req.Comment != null) fb.Comment = string.IsNullOrWhiteSpace(req.Comment) ? null : req.Comment.Trim();
            fb.UpdatedAt = DateTime.Now;

            await _uow.Feedbacks.UpdateAsync(fb);
            await _uow.SaveChangesAsync();

            // ✅ Chỉ lesson feedback (fb.LessonId != null) mới kéo theo tính lại rating tổng
            if (!string.IsNullOrEmpty(fb.ToUserId) && fb.LessonId != null)
            {
                await RecalculateTutorRatingAsync(fb.ToUserId!);
            }

            var fromName = (await _uow.Users.GetByIdAsync(fb.FromUserId!))?.UserName ?? "User";
            var toName = (await _uow.Users.GetByIdAsync(fb.ToUserId!))?.UserName ?? "User";
            return MapDto(fb, fromName, toName);
        }

        public async Task<bool> DeleteAsync(string actorUserId, string feedbackId)
        {
            var fb = await _uow.Feedbacks.GetByIdAsync(feedbackId)
                     ?? throw new KeyNotFoundException("Không tìm thấy feedback");

            if (fb.FromUserId != actorUserId)
                throw new UnauthorizedAccessException("Chỉ người viết feedback mới được xóa");

            await _uow.Feedbacks.RemoveAsync(fb);
            await _uow.SaveChangesAsync();

            // Nếu ToUser là tutor -> cập nhật rating
            await RecalculateTutorRatingAsync(fb.ToUserId!);

            return true;
        }

        public async Task<IEnumerable<FeedbackDto>> GetLessonFeedbacksAsync(string lessonId)
        {
            var items = await _uow.Feedbacks.GetByLessonAsync(lessonId);
            var rs = new List<FeedbackDto>();
            foreach (var f in items)
            {
                rs.Add(MapDto(f,
                    f.FromUser?.UserName ?? "User",
                    f.ToUser?.UserName ?? "User"));
            }
            return rs;
        }

        public async Task<(IEnumerable<FeedbackDto> items, int total)> GetTutorFeedbacksAsync(string tutorUserId, int page, int pageSize)
        {
            var (items, total) = await _uow.Feedbacks.GetByTutorUserAsync(tutorUserId, page, pageSize);
            var list = items.Select(f => MapDto(f,
                f.FromUser?.UserName ?? "User",
                f.ToUser?.UserName ?? "User")).ToList();
            return (list, total);
        }

        public async Task<TutorRatingSummaryDto> GetTutorRatingAsync(string tutorUserId)
        {
            var (avg, count) = await _uow.Feedbacks.CalcTutorRatingAsync(tutorUserId);
            return new TutorRatingSummaryDto
            {
                TutorUserId = tutorUserId,
                Average = Math.Round(avg, 2),
                Count = count
            };
        }

        // ===== Helpers =====

        private async Task EnsureParticipantPermission(User actor, string toUserId, Lesson lesson, string tutorUserId)
        {
            var actorRole = actor.RoleName;

            // Case A: Tutor đánh giá HS
            if (actorRole == "Tutor")
            {
                if (actor.Id != tutorUserId)
                    throw new UnauthorizedAccessException("Tutor không sở hữu buổi học này");

                // Nếu là lớp: toUserId phải là userId của 1 học sinh thuộc lớp
                if (lesson.ClassId != null)
                {
                    var isStudentInClass = await _ctx.ClassAssigns
                        .Include(x => x.Student)
                        .AnyAsync(x => x.ClassId == lesson.ClassId &&
                                       x.Student != null &&
                                       x.Student.UserId == toUserId);
                    if (!isStudentInClass)
                        throw new InvalidOperationException("Người nhận không thuộc lớp này");
                }
                // 1-1: bạn có thể thêm ràng buộc Lesson <-> student nếu có
                return;
            }

            // Case B: Student đánh giá Tutor
            if (actorRole == "Student")
            {
                // actor phải là student trong lớp
                if (lesson.ClassId == null)
                    throw new InvalidOperationException("Buổi học 1-1: bạn cần rule xác thực student tham gia");

                var stuProfile = await _uow.StudentProfiles.GetAsync(s => s.UserId == actor.Id)
                                 ?? throw new InvalidOperationException("Không tìm thấy profile HS");

                var isInClass = await _ctx.ClassAssigns
                    .AnyAsync(x => x.ClassId == lesson.ClassId && x.StudentId == stuProfile.Id);
                if (!isInClass) throw new UnauthorizedAccessException("Bạn không thuộc lớp của buổi học");

                if (toUserId != tutorUserId)
                    throw new InvalidOperationException("Student chỉ có thể đánh giá Tutor của buổi");
                return;
            }

            // Case C: Parent đánh giá Tutor (thay mặt con)
            if (actorRole == "Parent")
            {
                if (lesson.ClassId == null)
                    throw new InvalidOperationException("Buổi học 1-1: cần rule link parent-child cụ thể");

                // Parent phải có link tới 1 học sinh thuộc lớp
                var childIds = await _uow.ParentProfiles.GetChildrenIdsAsync(actor.Id);
                var studentInClass = await _ctx.ClassAssigns
                    .AnyAsync(x => x.ClassId == lesson.ClassId && x.StudentId != null && childIds.Contains(x.StudentId));
                if (!studentInClass)
                    throw new UnauthorizedAccessException("Phụ huynh không có học sinh tham gia lớp này");

                if (toUserId != tutorUserId)
                    throw new InvalidOperationException("Parent chỉ có thể đánh giá Tutor của buổi");
                return;
            }

            // Các role khác: chặn
            throw new UnauthorizedAccessException("Bạn không có quyền gửi feedback cho buổi này");
        }

        private async Task RecalculateTutorRatingAsync(string toUserId)
        {
            // Nếu người nhận là Tutor (User.RoleName == Tutor) -> ghi vào TutorProfile.Rating
            var toUser = await _uow.Users.GetByIdAsync(toUserId);
            if (toUser == null || toUser.RoleName != "Tutor") return;

            var (avg, _) = await _uow.Feedbacks.CalcTutorRatingAsync(toUserId);
            // 1 TutorUser : nhiều TutorProfile (theo db) -> lấy profile đầu?
            var profile = await _uow.TutorProfiles.GetAsync(t => t.UserId == toUserId);
            if (profile != null)
            {
                profile.Rating = Math.Round(avg, 2);
                await _uow.TutorProfiles.UpdateAsync(profile);
                await _uow.SaveChangesAsync();
            }
        }

        private static FeedbackDto MapDto(Feedback f, string fromName, string toName)
        {
            return new FeedbackDto
            {
                Id = f.Id,
                LessonId = f.LessonId ?? "",
                FromUserId = f.FromUserId ?? "",
                FromUserName = fromName,
                ToUserId = f.ToUserId ?? "",
                ToUserName = toName,
                Rating = f.Rating,
                Comment = f.Comment,
                CreatedAt = f.CreatedAt,
                UpdatedAt = f.UpdatedAt
            };
        }
    }
}
