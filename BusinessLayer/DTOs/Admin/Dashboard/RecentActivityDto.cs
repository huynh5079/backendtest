using System;

namespace BusinessLayer.DTOs.Admin.Dashboard
{
    public class RecentActivityDto
    {
        public string Id { get; set; } = default!;
        public string Type { get; set; } = default!; // "user_registration", "transaction", "tutor_application", "class_created", "report"
        public string Description { get; set; } = default!;
        public string Icon { get; set; } = default!; // "user", "lock", "warning", "check"
        public string Color { get; set; } = default!; // Hex color code
        public DateTime CreatedAt { get; set; }
    }
}

