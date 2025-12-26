using System.Collections.Generic;

namespace BusinessLayer.DTOs.Admin.Notification
{
    public class SendNotificationDto
    {
        /// <summary>
        /// Tiêu đề thông báo (bắt buộc)
        /// </summary>
        public string Title { get; set; } = default!;

        /// <summary>
        /// Nội dung thông báo (bắt buộc)
        /// </summary>
        public string Message { get; set; } = default!;

        /// <summary>
        /// Gửi cho user ID cụ thể (nếu có, ưu tiên hơn Role)
        /// </summary>
        public string? UserId { get; set; }

        /// <summary>
        /// Gửi cho user theo email (nếu có, sẽ được convert thành UserId)
        /// </summary>
        public string? UserEmail { get; set; }

        /// <summary>
        /// Gửi cho danh sách user IDs (nếu có, ưu tiên hơn Role)
        /// </summary>
        public List<string>? UserIds { get; set; }

        /// <summary>
        /// Gửi cho danh sách emails (nếu có, sẽ được convert thành UserIds)
        /// </summary>
        public List<string>? UserEmails { get; set; }

        /// <summary>
        /// Gửi cho role (Student, Tutor, Parent, Admin). Nếu null và không có UserId/UserIds, gửi cho tất cả users
        /// </summary>
        public string? Role { get; set; }

        /// <summary>
        /// ID entity liên quan (tùy chọn, ví dụ: ClassId)
        /// </summary>
        public string? RelatedEntityId { get; set; }
    }
}

