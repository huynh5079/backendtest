using BusinessLayer.DTOs.API;
using BusinessLayer.DTOs.Admin.Dashboard;
using BusinessLayer.DTOs.Admin.Notification;
using BusinessLayer.Service.Interface;
using DataLayer.Entities;
using DataLayer.Enum;
using DataLayer.Repositories.Abstraction;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace TPEdu_API.Controllers
{
    [ApiController]
    [Route("tpedu/v1/admin")]
    [Authorize(Roles = "Admin")]
    public class AdminDirectoryController : ControllerBase
    {
        private readonly IAdminDirectoryService _svc;
        private readonly IUnitOfWork _uow;
        private readonly INotificationService _notificationService;
        
        public AdminDirectoryController(
            IAdminDirectoryService svc,
            IUnitOfWork uow,
            INotificationService notificationService)
        {
            _svc = svc;
            _uow = uow;
            _notificationService = notificationService;
        }

        // GET tpedu/v1/admin/student?page=1
        [HttpGet("student")]
        public async Task<IActionResult> GetStudents([FromQuery] int page = 1)
        {
            // Validate page >= 1
            if (page < 1) page = 1;
            var rs = await _svc.GetStudentsPagedAsync(page, 5);
            var data = rs.Data.Select(x => new
            {
                studentId = x.StudentId,
                username = x.Username,
                email = x.Email,
                isBanned = x.IsBanned,
                createDate = x.CreateDate
            });
            return Ok(ApiResponse<object>.Ok(new
            {
                items = data,
                page = rs.Page,
                size = rs.PageSize,
                total = rs.TotalCount,
                totalPages = (int)Math.Ceiling((double)rs.TotalCount / rs.PageSize)
            }, "lấy danh sách thành công"));
        }

        // GET tpedu/v1/admin/student/detail/:id
        [HttpGet("student/detail/{id}")]
        public async Task<IActionResult> GetStudentDetail(string id)
        {
            var detail = await _svc.GetStudentDetailAsync(id);
            if (detail == null)
                return NotFound(ApiResponse<object>.Fail("bạn không có"));

            return Ok(ApiResponse<object>.Ok(detail, "lấy danh sách thành công"));
        }

        // GET tpedu/v1/admin/parent?page=1
        [HttpGet("parent")]
        public async Task<IActionResult> GetParents([FromQuery] int page = 1)
        {
            // Validate page >= 1
            if (page < 1) page = 1;
            var rs = await _svc.GetParentsPagedAsync(page, 5);
            var data = rs.Data.Select(x => new
            {
                parentId = x.ParentId,
                username = x.Username,
                email = x.Email,
                isBanned = x.IsBanned,
                createDate = x.CreateDate
            });
            return Ok(ApiResponse<object>.Ok(new
            {
                items = data,
                page = rs.Page,
                size = rs.PageSize,
                total = rs.TotalCount,
                totalPages = (int)Math.Ceiling((double)rs.TotalCount / rs.PageSize)
            }, "lấy danh sách thành công"));
        }

        // GET tpedu/v1/admin/parent/detail/:id
        [HttpGet("parent/detail/{id}")]
        public async Task<IActionResult> GetParentDetail(string id)
        {
            var detail = await _svc.GetParentDetailAsync(id);
            if (detail == null)
                return NotFound(ApiResponse<object>.Fail("bạn không có"));

            return Ok(ApiResponse<object>.Ok(detail, "lấy danh sách thành công"));
        }

        // GET tpedu/v1/admin/dashboard/statistics
        [HttpGet("dashboard/statistics")]
        public async Task<IActionResult> GetDashboardStatistics()
        {
            var statistics = await _svc.GetDashboardStatisticsAsync();
            return Ok(ApiResponse<object>.Ok(statistics, "Lấy thống kê dashboard thành công"));
        }

        /// <summary>
        /// Admin lấy danh sách hoạt động gần đây
        /// GET tpedu/v1/admin/dashboard/recent-activities?page=1&pageSize=5
        /// </summary>
        [HttpGet("dashboard/recent-activities")]
        public async Task<IActionResult> GetRecentActivities([FromQuery] int page = 1, [FromQuery] int pageSize = 5)
        {
            try
            {
                if (page < 1) page = 1;
                if (pageSize < 1 || pageSize > 50) pageSize = 5;

                var activities = new List<RecentActivityDto>();

                // 1. User registrations (mới đăng ký trong 7 ngày gần đây)
                var recentUsers = await _uow.Users.GetAllAsync(
                    u => u.CreatedAt >= DateTime.Now.AddDays(-7),
                    includes: q => q.Include(u => u.Role)
                );
                
                foreach (var user in recentUsers.OrderByDescending(u => u.CreatedAt).Take(10))
                {
                    var roleName = user.RoleName switch
                    {
                        "Student" => "Học viên",
                        "Tutor" => "Gia sư",
                        "Parent" => "Phụ huynh",
                        _ => user.RoleName
                    };
                    
                    activities.Add(new RecentActivityDto
                    {
                        Id = $"user_{user.Id}",
                        Type = "user_registration",
                        Description = $"Người dùng mới đăng ký ({roleName}): {user.UserName ?? user.Email ?? "N/A"}",
                        Icon = "user",
                        Color = "#9f7aea",
                        CreatedAt = user.CreatedAt
                    });
                }

                // 2. Transactions thành công (mới trong 7 ngày gần đây)
                var recentTransactions = await _uow.Transactions.GetAllAsync(
                    t => t.Status == TransactionStatus.Succeeded && t.CreatedAt >= DateTime.Now.AddDays(-7),
                    includes: q => q.Include(t => t.Wallet).ThenInclude(w => w.User)
                );

                foreach (var trans in recentTransactions
                    .Where(t => t.Type == TransactionType.Credit || t.Type == TransactionType.TransferIn)
                    .OrderByDescending(t => t.CreatedAt)
                    .Take(10))
                {
                    var amount = trans.Amount.ToString("N0") + " VNĐ";
                    var typeText = trans.Type == TransactionType.Credit ? "Nạp tiền" : "Nhận chuyển khoản";
                    
                    activities.Add(new RecentActivityDto
                    {
                        Id = $"transaction_{trans.Id}",
                        Type = "transaction",
                        Description = $"Giao dịch thành công: {typeText} - {amount}",
                        Icon = "lock",
                        Color = "#48bb78",
                        CreatedAt = trans.CreatedAt
                    });
                }

                // 3. Tutor applications pending (đang chờ duyệt)
                var allTutors = await _uow.TutorProfiles.GetAllAsync(
                    null,
                    includes: q => q.Include(t => t.User)
                );
                var pendingTutors = allTutors
                    .Where(t => t.User != null && t.User.Status == AccountStatus.PendingApproval)
                    .ToList();

                foreach (var tutor in pendingTutors.OrderByDescending(t => t.User!.CreatedAt).Take(10))
                {
                    activities.Add(new RecentActivityDto
                    {
                        Id = $"tutor_application_{tutor.Id}",
                        Type = "tutor_application",
                        Description = $"Đơn ứng tuyển gia sư đang chờ duyệt: {tutor.User?.UserName ?? tutor.User?.Email ?? "N/A"}",
                        Icon = "warning",
                        Color = "#ed8936",
                        CreatedAt = tutor.User!.CreatedAt
                    });
                }

                // 4. Reports mới (trong 7 ngày gần đây)
                var recentReports = await _uow.Reports.GetAllAsync(
                    r => r.CreatedAt >= DateTime.Now.AddDays(-7) && r.Status == ReportStatus.Pending,
                    includes: q => q.Include(r => r.Reporter)
                );

                foreach (var report in recentReports.OrderByDescending(r => r.CreatedAt).Take(10))
                {
                    var desc = report.Description ?? "N/A";
                    var shortDesc = desc.Length > 50 ? desc.Substring(0, 50) + "..." : desc;
                    
                    activities.Add(new RecentActivityDto
                    {
                        Id = $"report_{report.Id}",
                        Type = "report",
                        Description = $"Báo cáo mới: {shortDesc}",
                        Icon = "warning",
                        Color = "#f56565",
                        CreatedAt = report.CreatedAt
                    });
                }

                // Sắp xếp theo CreatedAt giảm dần
                var sortedActivities = activities
                    .OrderByDescending(a => a.CreatedAt)
                    .ToList();

                var total = sortedActivities.Count;
                var items = sortedActivities
                    .Skip((page - 1) * pageSize)
                    .Take(pageSize)
                    .ToList();

                return Ok(ApiResponse<object>.Ok(new
                {
                    items,
                    page,
                    pageSize,
                    total,
                    totalPages = (int)Math.Ceiling((double)total / pageSize)
                }, "Lấy danh sách hoạt động gần đây thành công"));
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error getting recent activities: {ex.Message}");
                return StatusCode(500, ApiResponse<object>.Fail($"Lỗi hệ thống: {ex.Message}"));
            }
        }

        /// <summary>
        /// Admin lấy danh sách thông báo đã gửi (system announcements)
        /// GET tpedu/v1/admin/notifications
        /// </summary>
        [HttpGet("notifications")]
        public async Task<IActionResult> GetNotificationsHistory([FromQuery] int page = 1, [FromQuery] int pageSize = 10)
        {
            try
            {
                if (page < 1) page = 1;
                if (pageSize < 1 || pageSize > 100) pageSize = 10;

                // Lấy tất cả SystemAnnouncement notifications
                var allNotifications = await _uow.Notifications.GetAllAsync(
                    n => n.Type == NotificationType.SystemAnnouncement,
                    includes: q => q.Include(n => n.User).ThenInclude(u => u.Role)
                );

                // Group theo Title + Message để tránh duplicate (cùng một thông báo gửi cho nhiều users)
                var grouped = allNotifications
                    .GroupBy(n => new { n.Title, n.Message, CreatedDate = n.CreatedAt.Date })
                    .Select(g => new
                    {
                        id = g.First().Id, // Lấy ID đầu tiên làm representative
                        title = g.Key.Title,
                        content = g.Key.Message,
                        // Xác định recipientType dựa trên role của users nhận
                        recipientType = g.Select(n => n.User?.RoleName)
                            .Distinct()
                            .Count() == 1 
                            ? (g.Select(n => n.User?.RoleName).FirstOrDefault() ?? "All")
                            : "All", // Nếu có nhiều role khác nhau thì là "All"
                        sentDate = g.Key.CreatedDate,
                        recipientCount = g.Count() // Số lượng người nhận
                    })
                    .OrderByDescending(x => x.sentDate)
                    .ThenByDescending(x => x.id)
                    .ToList();

                var total = grouped.Count;
                var items = grouped
                    .Skip((page - 1) * pageSize)
                    .Take(pageSize)
                    .ToList();

                return Ok(ApiResponse<object>.Ok(new
                {
                    items,
                    page,
                    pageSize,
                    total,
                    totalPages = (int)Math.Ceiling((double)total / pageSize)
                }, "Lấy danh sách thông báo thành công"));
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error getting notification history: {ex.Message}");
                return StatusCode(500, ApiResponse<object>.Fail($"Lỗi hệ thống: {ex.Message}"));
            }
        }

        /// <summary>
        /// Admin gửi thông báo cho user(s) hoặc role
        /// POST tpedu/v1/admin/notifications/send
        /// </summary>
        [HttpPost("notifications/send")]
        public async Task<IActionResult> SendNotification([FromBody] SendNotificationDto dto)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(dto.Title) || string.IsNullOrWhiteSpace(dto.Message))
                {
                    return BadRequest(ApiResponse<object>.Fail("Tiêu đề và nội dung thông báo không được để trống"));
                }

                var userIdsToNotify = new List<string>();

                // Ưu tiên 1: Gửi cho UserId cụ thể
                if (!string.IsNullOrWhiteSpace(dto.UserId))
                {
                    var user = await _uow.Users.GetByIdAsync(dto.UserId);
                    if (user == null)
                    {
                        return NotFound(ApiResponse<object>.Fail($"Không tìm thấy user với ID: {dto.UserId}"));
                    }
                    userIdsToNotify.Add(dto.UserId);
                }
                // Ưu tiên 1b: Gửi cho UserEmail cụ thể (convert email -> userId)
                else if (!string.IsNullOrWhiteSpace(dto.UserEmail))
                {
                    var user = (await _uow.Users.GetAllAsync(u => u.Email == dto.UserEmail.Trim())).FirstOrDefault();
                    if (user == null)
                    {
                        return NotFound(ApiResponse<object>.Fail($"Không tìm thấy user với email: {dto.UserEmail}"));
                    }
                    userIdsToNotify.Add(user.Id);
                }
                // Ưu tiên 2: Gửi cho danh sách UserIds
                else if (dto.UserIds != null && dto.UserIds.Any())
                {
                    var validUserIds = new List<string>();
                    foreach (var userId in dto.UserIds)
                    {
                        var user = await _uow.Users.GetByIdAsync(userId);
                        if (user != null)
                        {
                            validUserIds.Add(userId);
                        }
                    }
                    if (!validUserIds.Any())
                    {
                        return NotFound(ApiResponse<object>.Fail("Không tìm thấy user nào trong danh sách"));
                    }
                    userIdsToNotify = validUserIds;
                }
                // Ưu tiên 2b: Gửi cho danh sách emails (convert emails -> userIds)
                else if (dto.UserEmails != null && dto.UserEmails.Any())
                {
                    var validUserIds = new List<string>();
                    var emailList = dto.UserEmails.Select(e => e.Trim()).Where(e => !string.IsNullOrEmpty(e)).ToList();
                    
                    foreach (var email in emailList)
                    {
                        var user = (await _uow.Users.GetAllAsync(u => u.Email == email)).FirstOrDefault();
                        if (user != null)
                        {
                            validUserIds.Add(user.Id);
                        }
                    }
                    if (!validUserIds.Any())
                    {
                        return NotFound(ApiResponse<object>.Fail("Không tìm thấy user nào với các email đã nhập"));
                    }
                    userIdsToNotify = validUserIds;
                }
                // Ưu tiên 3: Gửi theo Role
                else if (!string.IsNullOrWhiteSpace(dto.Role))
                {
                    var users = await _uow.Users.GetAllAsync(u => u.RoleName == dto.Role);
                    userIdsToNotify = users.Select(u => u.Id).ToList();
                    if (!userIdsToNotify.Any())
                    {
                        return NotFound(ApiResponse<object>.Fail($"Không tìm thấy user nào với role: {dto.Role}"));
                    }
                }
                // Mặc định: Gửi cho tất cả users (trừ Admin)
                else
                {
                    var allUsers = await _uow.Users.GetAllAsync(u => u.RoleName != "Admin");
                    userIdsToNotify = allUsers.Select(u => u.Id).ToList();
                    if (!userIdsToNotify.Any())
                    {
                        return NotFound(ApiResponse<object>.Fail("Không tìm thấy user nào để gửi thông báo"));
                    }
                }

                // Tạo và gửi notification cho từng user
                var notificationsCreated = 0;
                foreach (var userId in userIdsToNotify)
                {
                    try
                    {
                        var notification = await _notificationService.CreateSystemAnnouncementNotificationAsync(
                            userId,
                            dto.Title,
                            dto.Message,
                            dto.RelatedEntityId);
                        await _notificationService.SendRealTimeNotificationAsync(userId, notification);
                        notificationsCreated++;
                    }
                    catch (Exception ex)
                    {
                        // Log lỗi nhưng tiếp tục với user tiếp theo
                        Console.WriteLine($"Failed to send notification to user {userId}: {ex.Message}");
                    }
                }

                await _uow.SaveChangesAsync();

                return Ok(ApiResponse<object>.Ok(
                    new { sentCount = notificationsCreated, totalRecipients = userIdsToNotify.Count },
                    $"Đã gửi thông báo thành công cho {notificationsCreated}/{userIdsToNotify.Count} người dùng"));
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error sending notification: {ex.Message}");
                Console.WriteLine($"StackTrace: {ex.StackTrace}");
                return StatusCode(500, ApiResponse<object>.Fail($"Lỗi hệ thống: {ex.Message}"));
            }
        }
    }

}
