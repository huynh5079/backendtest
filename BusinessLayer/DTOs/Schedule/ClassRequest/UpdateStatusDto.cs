using DataLayer.Enum;
using System.ComponentModel.DataAnnotations;

namespace BusinessLayer.DTOs.Schedule.ClassRequest
{
    public class UpdateStatusDto
    {
        public ClassRequestStatus Status { get; set; } = ClassRequestStatus.Pending; // Default value để tránh lỗi khi chỉ gửi MeetingLink

        // Add for link tranfer
        public string? MeetingLink { get; set; }
    }
}