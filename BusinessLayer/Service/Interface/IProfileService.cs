using BusinessLayer.DTOs.Media;
using BusinessLayer.DTOs.Profile;
using Microsoft.AspNetCore.Http;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace BusinessLayer.Service.Interface
{
    public interface IProfileService
    {
        Task<StudentProfileDto> GetStudentProfileAsync(string userId);
        Task<ParentProfileDto> GetParentProfileAsync(string userId);
        Task<TutorProfileDto> GetTutorProfileAsync(string userId);
        Task UpdateStudentAsync(string userId, UpdateStudentProfileRequest dto, CancellationToken ct = default);
        Task UpdateParentAsync(string userId, UpdateParentProfileRequest dto, CancellationToken ct = default);
        Task UpdateTutorAsync(string userId, UpdateTutorProfileRequest dto, CancellationToken ct = default);
        Task<string> UpdateAvatarAsync(string userId, IFormFile avatarFile, CancellationToken ct = default);

        // Certificate management
        Task<List<MediaItemDto>> UploadTutorCertificatesAsync(string userId, List<IFormFile> certificates, CancellationToken ct = default);
        Task DeleteTutorCertificateAsync(string userId, string mediaId, CancellationToken ct = default);

        // Identity Document management
        Task<List<MediaItemDto>> UploadTutorIdentityDocumentsAsync(string userId, List<IFormFile> documents, CancellationToken ct = default);
        Task DeleteTutorIdentityDocumentAsync(string userId, string mediaId, CancellationToken ct = default);
    }
}
