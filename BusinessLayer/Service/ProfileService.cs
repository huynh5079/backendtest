using BusinessLayer.DTOs.Media;
using BusinessLayer.DTOs.Profile;
using BusinessLayer.Service.Interface;
using BusinessLayer.Storage;
using BusinessLayer.Utils;
using DataLayer.Entities;
using DataLayer.Enum;
using DataLayer.Repositories.Abstraction;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace BusinessLayer.Service
{
    public class ProfileService : IProfileService
    {
        private readonly IUnitOfWork _uow;
        private readonly IFileStorageService _storage;
        private readonly IMediaService _media;

        public ProfileService(IUnitOfWork uow, IFileStorageService storage, IMediaService media)
        {
            _uow = uow; 
            _storage = storage; 
            _media = media;
        }

        public async Task<StudentProfileDto> GetStudentProfileAsync(string userId)
        {
            var u = await _uow.Users.GetByIdAsync(userId)
                    ?? throw new KeyNotFoundException("không tìm thấy user");
            if (u.RoleName != nameof(RoleEnum.Student))
                throw new InvalidOperationException("user không phải Student");

            var sp = await _uow.StudentProfiles.GetByUserIdAsync(userId);
            // sp có thể null nếu chưa provision – vẫn trả user basic
            return new StudentProfileDto
            {
                StudentUserId = userId,
                StudentProfileId = sp?.Id,
                Username = u.UserName,
                Email = u.Email,
                Phone = u.Phone,
                Gender = u.Gender?.ToString().ToLowerInvariant(),
                Address = u.Address,
                DateOfBirth = u.DateOfBirth,
                AvatarUrl = u.AvatarUrl,

                EducationLevel = sp?.EducationLevel,
                PreferredSubjects = sp?.PreferredSubjects
            };
        }

        public async Task<ParentProfileDto> GetParentProfileAsync(string userId)
        {
            var u = await _uow.Users.GetByIdAsync(userId)
                    ?? throw new KeyNotFoundException("không tìm thấy user");
            if (u.RoleName != nameof(RoleEnum.Parent))
                throw new InvalidOperationException("user không phải Parent");

            // ✅ Lấy toàn bộ con của parent
            var children = await _uow.ParentProfiles.GetChildrenAllAsync(userId);

            var dto = new ParentProfileDto
            {
                Username = u.UserName,
                Email = u.Email,
                Phone = u.Phone,
                Gender = u.Gender?.ToString().ToLowerInvariant(),
                Address = u.Address,
                DateOfBirth = u.DateOfBirth,
                AvatarUrl = u.AvatarUrl,

                // ✅ Map mảng children
                Children = children.Select(x => new ParentChildBriefDto
                {
                    StudentId = x.stu.Id,
                    StudentUserId = x.childUser.Id,
                    Username = x.childUser.UserName,
                    Email = x.childUser.Email!,
                    AvatarUrl = x.childUser.AvatarUrl,
                    CreateDate = x.stu.CreatedAt,
                    Relationship = x.link.Relationship,
                    EducationLevel = x.stu.EducationLevel   // lưu dạng chuỗi
                }).ToList()
            };

            return dto;
        }

        public async Task<TutorProfileDto> GetTutorProfileAsync(string userId)
        {
            var u = await _uow.Users.GetByIdAsync(userId)
                    ?? throw new KeyNotFoundException("không tìm thấy user");
            if (u.RoleName != nameof(RoleEnum.Tutor))
                throw new InvalidOperationException("user không phải Tutor");

            var tp = await _uow.TutorProfiles.GetByUserIdAsync(userId);

            var dto = new TutorProfileDto
            {
                TutorProfileId = tp?.Id,
                Username = u.UserName,
                Email = u.Email,
                Phone = u.Phone,
                Gender = u.Gender?.ToString().ToLowerInvariant(),
                Address = u.Address,
                DateOfBirth = u.DateOfBirth,
                AvatarUrl = u.AvatarUrl,

                Bio = tp?.Bio,
                EducationLevel = tp?.EducationLevel,
                University = tp?.University,
                Major = tp?.Major,
                TeachingExperienceYears = tp?.TeachingExperienceYears,
                TeachingSubjects = tp?.TeachingSubjects,
                TeachingLevel = tp?.TeachingLevel,
                SpecialSkills = tp?.SpecialSkills,
                Rating = tp?.Rating,
                ReviewStatus = tp?.ReviewStatus.ToString()
            };

            // Media (nếu cần)
            if (tp != null)
            {
                var certs = await _uow.Media.GetCertificatesByTutorProfileAsync(tp.Id!);
                dto.Certificates = certs.Select(m => new MediaItemDto
                {
                    Id = m.Id,
                    Url = m.FileUrl,
                    FileName = m.FileName,
                    ContentType = m.MediaType,
                    FileSize = m.FileSize
                }).ToList();
            }

            var identityDocs = await _uow.Media.GetByOwnerAsync(userId);
            dto.IdentityDocuments = identityDocs
                .Where(m => m.Context == UploadContext.IdentityDocument)
                .Select(m => new MediaItemDto
                {
                    Id = m.Id,
                    Url = m.FileUrl,
                    FileName = m.FileName,
                    ContentType = m.MediaType,
                    FileSize = m.FileSize
                }).ToList();

            return dto;
        }

        public async Task UpdateStudentAsync(string userId, UpdateStudentProfileRequest dto, CancellationToken ct = default)
        {
            var user = await _uow.Users.GetByIdAsync(userId) ?? throw new InvalidOperationException("User không tồn tại");
            if (user.RoleName != nameof(RoleEnum.Student))
                throw new InvalidOperationException("Chỉ student mới dùng API này");

            if (!string.IsNullOrWhiteSpace(dto.Username)) user.UserName = dto.Username.Trim();
            if (!string.IsNullOrWhiteSpace(dto.Phone)) user.Phone = dto.Phone.Trim();
            if (!string.IsNullOrWhiteSpace(dto.Gender))
            {
                if (Enum.TryParse<Gender>(dto.Gender, true, out var g)) user.Gender = g;
            }
            if (!string.IsNullOrWhiteSpace(dto.Address)) user.Address = dto.Address.Trim();

            if (dto.DateOfBirth.HasValue)
            {
                AgeUtil.ValidateDob(dto.DateOfBirth);
                user.DateOfBirth = dto.DateOfBirth;
            }

            await _uow.Users.UpdateAsync(user);

            var sp = await _uow.StudentProfiles.GetByUserIdAsync(userId) ?? new StudentProfile { UserId = userId };
            if (dto.EducationLevel != null) sp.EducationLevel = dto.EducationLevel;
            if (dto.PreferredSubjects != null) sp.PreferredSubjects = dto.PreferredSubjects;

            if (string.IsNullOrEmpty(sp.Id))
                await _uow.StudentProfiles.CreateAsync(sp);
            else
                await _uow.StudentProfiles.UpdateAsync(sp);

            await _uow.SaveChangesAsync();
        }

        public async Task UpdateParentAsync(string userId, UpdateParentProfileRequest dto, CancellationToken ct = default)
        {
            var user = await _uow.Users.GetByIdAsync(userId) ?? throw new InvalidOperationException("User không tồn tại");
            if (user.RoleName != nameof(RoleEnum.Parent))
                throw new InvalidOperationException("Chỉ parent mới dùng API này");

            if (!string.IsNullOrWhiteSpace(dto.Username)) user.UserName = dto.Username.Trim();
            if (!string.IsNullOrWhiteSpace(dto.Phone)) user.Phone = dto.Phone.Trim();
            if (!string.IsNullOrWhiteSpace(dto.Gender))
            {
                if (Enum.TryParse<Gender>(dto.Gender, true, out var g)) user.Gender = g;
            }
            if (!string.IsNullOrWhiteSpace(dto.Address)) user.Address = dto.Address.Trim();

            if (dto.DateOfBirth.HasValue)
            {
                AgeUtil.ValidateDob(dto.DateOfBirth);
                user.DateOfBirth = dto.DateOfBirth;
            }

            await _uow.Users.UpdateAsync(user);
            await _uow.SaveChangesAsync();
        }

        public async Task UpdateTutorAsync(string userId, UpdateTutorProfileRequest dto, CancellationToken ct = default)
        {
            var user = await _uow.Users.GetByIdAsync(userId) ?? throw new InvalidOperationException("User không tồn tại");
            if (user.RoleName != nameof(RoleEnum.Tutor))
                throw new InvalidOperationException("Chỉ tutor mới dùng API này");

            if (!string.IsNullOrWhiteSpace(dto.Username)) user.UserName = dto.Username.Trim();
            if (!string.IsNullOrWhiteSpace(dto.Phone)) user.Phone = dto.Phone.Trim();
            if (!string.IsNullOrWhiteSpace(dto.Gender))
            {
                if (Enum.TryParse<Gender>(dto.Gender, true, out var g)) user.Gender = g;
            }
            if (!string.IsNullOrWhiteSpace(dto.Address)) user.Address = dto.Address.Trim();

            if (dto.DateOfBirth.HasValue)
            {
                AgeUtil.ValidateDob(dto.DateOfBirth);
                user.DateOfBirth = dto.DateOfBirth;
            }

            await _uow.Users.UpdateAsync(user);

            var tp = await _uow.TutorProfiles.GetByUserIdAsync(userId) ?? new TutorProfile { UserId = userId };
            if (dto.Bio != null) tp.Bio = dto.Bio;
            if (dto.EducationLevel != null) tp.EducationLevel = dto.EducationLevel;
            if (dto.University != null) tp.University = dto.University;
            if (dto.Major != null) tp.Major = dto.Major;
            if (dto.TeachingExperienceYears.HasValue) tp.TeachingExperienceYears = dto.TeachingExperienceYears;
            if (dto.TeachingSubjects != null) tp.TeachingSubjects = string.Join(",", dto.TeachingSubjects);
            if (dto.TeachingLevel != null) tp.TeachingLevel = string.Join(",", dto.TeachingLevel);
            if (dto.SpecialSkills != null) tp.SpecialSkills = string.Join(",", dto.SpecialSkills);

            if (string.IsNullOrEmpty(tp.Id))
                await _uow.TutorProfiles.CreateAsync(tp);
            else
                await _uow.TutorProfiles.UpdateAsync(tp);

            if (dto.NewCertificates is { Count: > 0 })
            {
                var ups = await _storage.UploadManyAsync(dto.NewCertificates, UploadContext.Certificate, userId, ct);
                await _media.SaveTutorCertificatesAsync(userId, tp.Id!, ups);
            }

            await _uow.SaveChangesAsync();
        }

        public async Task<string> UpdateAvatarAsync(string userId, IFormFile avatarFile, CancellationToken ct = default)
        {
            // Validate avatar file
            Helper.AvatarHelper.ValidateAvatarFile(avatarFile);

            // Get user
            var user = await _uow.Users.GetByIdAsync(userId) 
                ?? throw new KeyNotFoundException("Không tìm thấy user.");

            // Upload avatar
            var uploads = await _storage.UploadManyAsync(new[] { avatarFile }, UploadContext.Avatar, userId, ct);
            var newAvatarUrl = uploads.First().Url;

            // Update user avatar URL
            user.AvatarUrl = newAvatarUrl;
            await _uow.Users.UpdateAsync(user);
            await _uow.SaveChangesAsync();

            return newAvatarUrl;
        }

        // ========== CERTIFICATE MANAGEMENT ==========

        public async Task<List<MediaItemDto>> UploadTutorCertificatesAsync(
            string userId, List<IFormFile> certificates, CancellationToken ct = default)
        {
            // 1. Validate user is Tutor
            var tutor = await _uow.TutorProfiles.GetByUserIdAsync(userId)
                ?? throw new UnauthorizedAccessException("Chỉ gia sư mới có thể upload chứng chỉ");

            // 2. Upload files
            var uploads = await _storage.UploadManyAsync(certificates, UploadContext.Certificate, userId, ct);

            // 3. Save to database
            var mediaList = await _media.SaveTutorCertificatesAsync(userId, tutor.Id!, uploads);

            // 5. Map to DTO
            return mediaList.Select(m => new MediaItemDto
            {
                Id = m.Id,
                Url = m.FileUrl,
                FileName = m.FileName,
                ContentType = m.MediaType,
                FileSize = m.FileSize
            }).ToList();
        }

        public async Task DeleteTutorCertificateAsync(string userId, string mediaId, CancellationToken ct = default)
        {
            // 1. Get media record
            var media = await _uow.Media.GetByIdAsync(mediaId)
                ?? throw new KeyNotFoundException("Không tìm thấy chứng chỉ");

            // 2. Validate ownership
            if (media.OwnerUserId != userId)
                throw new UnauthorizedAccessException("Bạn không có quyền xóa chứng chỉ này");

            // 3. Validate context
            if (media.Context != UploadContext.Certificate)
                throw new InvalidOperationException("File này không phải là chứng chỉ");

            // 4. Delete from cloud storage
            if (!string.IsNullOrEmpty(media.ProviderPublicId))
            {
                await _storage.DeleteAsync(media.ProviderPublicId, media.MediaType, ct);
            }

            // 5. Delete from database
            await _uow.Media.RemoveAsync(media);
            await _uow.SaveChangesAsync();
        }

        // ========== IDENTITY DOCUMENT MANAGEMENT ==========

        public async Task<List<MediaItemDto>> UploadTutorIdentityDocumentsAsync(
            string userId, List<IFormFile> documents, CancellationToken ct = default)
        {
            // 1. Validate user exists
            var user = await _uow.Users.GetByIdAsync(userId)
                ?? throw new KeyNotFoundException("Không tìm thấy người dùng");

            // 2. Validate role (Tutor only)
            if (user.RoleName != "Tutor")
                throw new UnauthorizedAccessException("Chỉ gia sư mới có thể upload giấy tờ tùy thân");

            // 3. Upload files
            var uploads = await _storage.UploadManyAsync(documents, UploadContext.IdentityDocument, userId, ct);

            // 4. Save to database
            var mediaList = await _media.SaveTutorIdentityDocsAsync(userId, uploads);

            // 6. Map to DTO
            return mediaList.Select(m => new MediaItemDto
            {
                Id = m.Id,
                Url = m.FileUrl,
                FileName = m.FileName,
                ContentType = m.MediaType,
                FileSize = m.FileSize
            }).ToList();
        }

        public async Task DeleteTutorIdentityDocumentAsync(string userId, string mediaId, CancellationToken ct = default)
        {
            // 1. Get media record
            var media = await _uow.Media.GetByIdAsync(mediaId)
                ?? throw new KeyNotFoundException("Không tìm thấy giấy tờ");

            // 2. Validate ownership
            if (media.OwnerUserId != userId)
                throw new UnauthorizedAccessException("Bạn không có quyền xóa giấy tờ này");

            // 3. Validate context
            if (media.Context != UploadContext.IdentityDocument)
                throw new InvalidOperationException("File này không phải là giấy tờ tùy thân");

            // 4. Delete from cloud storage
            if (!string.IsNullOrEmpty(media.ProviderPublicId))
            {
                await _storage.DeleteAsync(media.ProviderPublicId, media.MediaType, ct);
            }

            // 5. Delete from database
            await _uow.Media.RemoveAsync(media);
            await _uow.SaveChangesAsync();
        }
    }
}
