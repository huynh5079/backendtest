using BusinessLayer.DTOs.Admin.Tutors;
using BusinessLayer.Service.Interface;
using DataLayer.Entities;
using DataLayer.Repositories.Abstraction;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace BusinessLayer.Service
{
    public class PublicTutorService : IPublicTutorService
    {
        private readonly IUnitOfWork _uow;
        public PublicTutorService(IUnitOfWork uow) => _uow = uow;

        public async Task<PaginationResult<PublicTutorListItemDto>> GetApprovedTutorsPagedAsync(int page, int pageSize = 6)
        {
            var rs = await _uow.TutorProfiles.GetApprovedPagedAsync(page, pageSize);
            
            var mapped = new List<PublicTutorListItemDto>();
            foreach (var tp in rs.Data)
            {
                // Tính rating và feedbackCount động cho mỗi tutor
                var (calculatedRating, feedbackCount) = await _uow.Feedbacks.CalcTutorRatingAsync(tp.UserId!);
                
                mapped.Add(new PublicTutorListItemDto
                {
                    TutorId = tp.UserId!,
                    Username = tp.User?.UserName,
                    Email = tp.User?.Email ?? "",
                    TeachingSubjects = tp.TeachingSubjects,
                    TeachingLevel = tp.TeachingLevel,
                    CreateDate = tp.User!.CreatedAt,
                    AvatarUrl = tp.User!.AvatarUrl,
                    Rating = calculatedRating > 0 ? calculatedRating : null, // Dùng rating tính được, null nếu chưa có feedback
                    FeedbackCount = feedbackCount,
                    Address = tp.User?.Address
                });
            }
            return new PaginationResult<PublicTutorListItemDto>(mapped, rs.TotalCount, rs.PageNumber, rs.PageSize);
        }

        public async Task<PaginationResult<PublicTutorListItemDto>> SearchAndFilterTutorsAsync(TutorSearchFilterDto filter)
        {
            var rs = await _uow.TutorProfiles.SearchAndFilterApprovedAsync(
                filter.Keyword,
                filter.Subject,
                filter.EducationLevel,
                filter.Mode,
                filter.Area,
                filter.Gender,
                filter.MinRating,
                filter.MinPrice,
                filter.MaxPrice,
                filter.Page,
                filter.PageSize);

            var mapped = new List<PublicTutorListItemDto>();
            foreach (var tp in rs.Data)
            {
                // Tính rating và feedbackCount động cho mỗi tutor
                var (calculatedRating, feedbackCount) = await _uow.Feedbacks.CalcTutorRatingAsync(tp.UserId!);
                
                mapped.Add(new PublicTutorListItemDto
                {
                    TutorId = tp.UserId!,
                    Username = tp.User?.UserName,
                    Email = tp.User?.Email ?? "",
                    TeachingSubjects = tp.TeachingSubjects,
                    TeachingLevel = tp.TeachingLevel,
                    CreateDate = tp.User!.CreatedAt,
                    AvatarUrl = tp.User!.AvatarUrl,
                    Rating = calculatedRating > 0 ? calculatedRating : null, // Dùng rating tính được
                    FeedbackCount = feedbackCount,
                    Address = tp.User?.Address
                });
            }
            return new PaginationResult<PublicTutorListItemDto>(mapped, rs.TotalCount, rs.PageNumber, rs.PageSize);
        }

        public async Task<PublicTutorDetailDto?> GetApprovedTutorDetailAsync(string userId)
        {
            var tp = await _uow.TutorProfiles.GetApprovedByUserIdAsync(userId);
            if (tp == null) return null;

            var u = tp.User!;
            
            // Tính rating động từ Feedback table
            var (calculatedRating, feedbackCount) = await _uow.Feedbacks.CalcTutorRatingAsync(userId);
            
            return new PublicTutorDetailDto
            {
                TutorId = u.Id,
                TutorProfileId = tp.Id,
                Username = u.UserName,
                Email = u.Email!,
                AvatarUrl = u.AvatarUrl,

                Gender = u.Gender?.ToString().ToLowerInvariant(),
                DateOfBirth = u.DateOfBirth,
                Address = u.Address, // Địa chỉ gia sư để học sinh xem khi đặt

                EducationLevel = tp.EducationLevel,
                University = tp.University,
                Major = tp.Major,

                TeachingExperienceYears = tp.TeachingExperienceYears,
                TeachingSubjects = tp.TeachingSubjects,
                TeachingLevel = tp.TeachingLevel,
                Bio = tp.Bio,
                Rating = calculatedRating > 0 ? calculatedRating : null, // Tính từ Feedback, null nếu chưa có

                CreateDate = u.CreatedAt
            };
        }

        /// <summary>
        /// Lấy top N tutors có rating cao nhất (tính từ Feedback table)
        /// </summary>
        public async Task<IReadOnlyList<PublicTutorListItemDto>> GetTopRatedTutorsAsync(int count = 3)
        {
            var topTutors = await _uow.TutorProfiles.GetTopRatedAsync(count);
            
            var result = new List<PublicTutorListItemDto>();
            
            foreach (var tp in topTutors)
            {
                // Tính rating và feedbackCount động cho mỗi tutor
                var (calculatedRating, feedbackCount) = await _uow.Feedbacks.CalcTutorRatingAsync(tp.UserId!);
                
                // Chỉ thêm tutors có rating > 0
                if (calculatedRating > 0)
                {
                    result.Add(new PublicTutorListItemDto
                    {
                        TutorId = tp.UserId!,
                        Username = tp.User?.UserName,
                        Email = tp.User?.Email ?? "",
                        TeachingSubjects = tp.TeachingSubjects,
                        TeachingLevel = tp.TeachingLevel,
                        CreateDate = tp.User!.CreatedAt,
                        AvatarUrl = tp.User!.AvatarUrl,
                        Rating = calculatedRating,
                        FeedbackCount = feedbackCount,
                        Address = tp.User?.Address
                    });
                }
            }
            
            // Sort by rating descending và lấy top N
            return result
                .OrderByDescending(x => x.Rating)
                .ThenByDescending(x => x.CreateDate)
                .Take(count)
                .ToList();
        }
    }
}

