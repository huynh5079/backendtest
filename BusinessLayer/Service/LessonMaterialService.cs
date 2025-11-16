using BusinessLayer.DTOs.LessonMaterials;
using BusinessLayer.Service.Interface;
using BusinessLayer.Storage;
using DataLayer.Entities;
using DataLayer.Enum;
using DataLayer.Repositories.Abstraction;
using DataLayer.Repositories.Abstraction.Schedule;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace BusinessLayer.Service
{
    public class LessonMaterialService : ILessonMaterialService
    {
        private readonly IUnitOfWork _uow;
        private readonly IFileStorageService _storage;
        private readonly IAiAnalysisService _aiAnalysisService; 
        private readonly ILogger<LessonMaterialService> _logger; 

        public LessonMaterialService(IUnitOfWork uow, IFileStorageService storage, IAiAnalysisService aiAnalysisService, ILogger<LessonMaterialService> logger)
        {
            _uow = uow;
            _storage = storage;
            _aiAnalysisService = aiAnalysisService;
            _logger = logger;
        }

        private static MaterialItemDto Map(Media m) => new()
        {
            Id = m.Id,
            FileName = m.FileName,
            Url = m.FileUrl,
            MediaType = m.MediaType,
            FileSize = m.FileSize,
            CreatedAt = m.CreatedAt,
            UploadedByUserId = m.OwnerUserId
        };

        // ===== List (Tutor/Student/Parent) =====
        public async Task<IReadOnlyList<MaterialItemDto>> ListAsync(string actorUserId, string lessonId)
        {
            var (lesson, cls) = await _uow.Lessons.GetWithClassAsync(lessonId);

            // Quyền xem: Tutor chủ lớp hoặc Student đã ghi danh
            var tutorUserId = await _uow.TutorProfiles.GetTutorUserIdByTutorProfileIdAsync(cls.TutorId);
            var isTutorOwner = tutorUserId == actorUserId;

            var isAllowed = isTutorOwner; // Bắt đầu bằng quyền Tutor

            if (!isAllowed)
            {
                // Thử check quyền Student
                var studentProfileId = await _uow.StudentProfiles.GetIdByUserIdAsync(actorUserId);
                if (studentProfileId != null)
                {
                    isAllowed = await _uow.ClassAssigns.IsApprovedAsync(cls.Id, studentProfileId);
                }
            }

            if (!isAllowed)
            {
                // Nếu vẫn không được, thử check quyền Parent
                var user = await _uow.Users.GetByIdAsync(actorUserId);
                if (user?.RoleName == "Parent")
                {
                    // Lấy ID các con của phụ huynh
                    var childIds = await _uow.ParentProfiles.GetChildrenIdsAsync(actorUserId);
                    if (childIds.Any())
                    {
                        // Kiểm tra xem có bất kỳ đứa con nào học lớp này không
                        isAllowed = await _uow.ClassAssigns.IsAnyChildApprovedAsync(cls.Id, childIds);
                    }
                }
            }

            // Kiểm tra lần cuối
            if (!isAllowed)
                throw new UnauthorizedAccessException("Bạn không có quyền xem tài liệu buổi học này.");

            var items = await _uow.Media.GetAllAsync(
                filter: m => m.LessonId == lessonId
                          && m.Context == UploadContext.Material
                          && m.DeletedAt == null,
                includes: q => q.OrderByDescending(m => m.CreatedAt)
            );

            return items.Select(Map).ToList();
        }

        // ===== Upload files (Tutor) =====
        public async Task<IReadOnlyList<MaterialItemDto>> UploadAsync(
            string tutorUserId, string lessonId, IEnumerable<IFormFile> files, CancellationToken ct)
        {
            var (lesson, cls) = await _uow.Lessons.GetWithClassAsync(lessonId);
            var tutorUserIdOfClass = await _uow.TutorProfiles.GetTutorUserIdByTutorProfileIdAsync(cls.TutorId);
            if (tutorUserIdOfClass != tutorUserId)
                throw new UnauthorizedAccessException("Chỉ gia sư của lớp mới được upload tài liệu.");

            var ups = await _storage.UploadManyAsync(files, UploadContext.Material, tutorUserId, ct);

            var list = new List<Media>();
            foreach (var up in ups)
            {
                var m = new Media
                {
                    FileUrl = up.Url,
                    FileName = up.FileName,
                    MediaType = up.ContentType,
                    FileSize = up.FileSize,
                    OwnerUserId = tutorUserId,
                    Context = UploadContext.Material,
                    LessonId = lessonId,
                    ProviderPublicId = up.ProviderPublicId
                };
                await _uow.Media.CreateAsync(m);
                list.Add(m);

                try
                {
                    string context = $"Chủ đề Lớp: {cls.Title}. Chủ đề Bài học: {lesson.Title}.";

                    // TẠO PROMPT KIỂM DUYỆT
                    string moderationPrompt = $"Bạn là trợ lý kiểm duyệt. Hãy phân tích file sau đây. Chủ đề yêu cầu là: '{context}'. " +
                                              "Nội dung file có liên quan đến chủ đề này không? File có chứa nội dung nhạy cảm (var) không? Trả lời ngắn gọn.";

                    // Gọi AI Service (SỬA TÊN HÀM Ở ĐÂY)
                    _logger.LogInformation("Bắt đầu phân tích AI cho file: {FileName}", up.FileName);
                    string analysisResult = await _aiAnalysisService.AnalyzeFileAsync(
                        moderationPrompt,
                        up.Url,
                        up.ContentType
                    );
                    _logger.LogInformation("Kết quả phân tích AI cho {FileName}: {Result}", up.FileName, analysisResult);

                    // Xử lý kết quả (Tự động tạo Report nếu vi phạm)
                    if (analysisResult.ToUpper().Contains("KHÔNG LIÊN QUAN") ||
                        analysisResult.ToUpper().Contains("NHẠY CẢM") ||
                        analysisResult.ToUpper().Contains("SAI CHỦ ĐỀ"))
                    {
                        var report = new Report
                        {
                            ReporterId = "system-ai",
                            TargetUserId = tutorUserIdOfClass,
                            TargetLessonId = lessonId,
                            TargetMediaId = m.Id,
                            Description = $"[AI Tự động] File tài liệu '{up.FileName}' bị nghi ngờ. Lý do: {analysisResult}",
                            Status = ReportStatus.Pending
                        };
                        await _uow.Reports.CreateAsync(report);
                        _logger.LogWarning("AI đã tạo Report cho file {FileName} (MediaId: {MediaId})", up.FileName, m.Id);
                    }
                }
                catch (Exception aiEx)
                {
                    _logger.LogError(aiEx, "Lỗi phân tích AI khi upload tài liệu {FileName} cho lesson {LessonId}", up.FileName, lessonId);
                }
            }
            await _uow.SaveChangesAsync();
            return list.Select(Map).ToList();
        }

        // ===== Add links (Tutor) =====
        public async Task<IReadOnlyList<MaterialItemDto>> AddLinksAsync(
            string tutorUserId, string lessonId, IEnumerable<(string url, string? title)> links)
        {
            var (lesson, cls) = await _uow.Lessons.GetWithClassAsync(lessonId);
            var tutorUserIdOfClass = await _uow.TutorProfiles.GetTutorUserIdByTutorProfileIdAsync(cls.TutorId);
            if (tutorUserIdOfClass != tutorUserId)
                throw new UnauthorizedAccessException("Chỉ gia sư của lớp mới được chèn link.");

            var created = new List<Media>();
            foreach (var (url, title) in links)
            {
                var m = new Media
                {
                    FileUrl = url,
                    FileName = string.IsNullOrWhiteSpace(title) ? "Link" : title!,
                    MediaType = "link/url",
                    FileSize = 0,
                    OwnerUserId = tutorUserId,
                    Context = UploadContext.Material,
                    LessonId = lessonId
                };
                await _uow.Media.CreateAsync(m);
                created.Add(m);
            }
            await _uow.SaveChangesAsync();
            return created.Select(Map).ToList();
        }

        // ===== Delete (Tutor) =====
        public async Task<bool> DeleteAsync(string tutorUserId, string lessonId, string mediaId, CancellationToken ct)
        {
            var (lesson, cls) = await _uow.Lessons.GetWithClassAsync(lessonId);
            var tutorUserIdOfClass = await _uow.TutorProfiles.GetTutorUserIdByTutorProfileIdAsync(cls.TutorId);
            if (tutorUserIdOfClass != tutorUserId)
                throw new UnauthorizedAccessException("Chỉ gia sư của lớp mới được xoá tài liệu.");

            var media = await _uow.Media.GetAsync(m =>
                m.Id == mediaId &&
                m.LessonId == lessonId &&
                m.Context == UploadContext.Material &&
                m.DeletedAt == null
            );

            if (media == null) return false;

            media.DeletedAt = DateTime.Now;  // Soft delete
            await _uow.Media.UpdateAsync(media);
            await _uow.SaveChangesAsync();
            return true;
        }
    }
}
