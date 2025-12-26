using DataLayer.Enum;
using System;

namespace BusinessLayer.DTOs.Schedule.ClassAssign
{
    public class StudentEnrollmentDto
    {
        public string StudentId { get; set; } = null!;
        public string? StudentUserId { get; set; } // UserId của học sinh (để nhắn tin)
        public string StudentName { get; set; } = null!;
        public string? StudentEmail { get; set; }
        public string? StudentAvatarUrl { get; set; }
        public string? StudentPhone { get; set; }
        public ApprovalStatus ApprovalStatus { get; set; }
        public PaymentStatus PaymentStatus { get; set; }
        public DateTime? EnrolledAt { get; set; }
        public DateTime CreatedAt { get; set; }
    }
}

