using DataLayer.Enum;

namespace BusinessLayer.DTOs.Reports
{
    /// <summary>
    /// Auto-report item for listing
    /// </summary>
    public class AutoReportItemDto
    {
        public string Id { get; set; } = string.Empty;
        public string ReporterId { get; set; } = string.Empty;
        public string ReporterName { get; set; } = string.Empty;
        public string ClassId { get; set; } = string.Empty;
        public string ClassName { get; set; } = string.Empty;
        public int AbsentCount { get; set; }
        public int TotalLessons { get; set; }
        public double AbsenceRate { get; set; }
        public ReportStatus Status { get; set; }
        public StudentResponseAction? StudentResponse { get; set; }
        public DateTime? StudentRespondedAt { get; set; }
        public DateTime CreatedAt { get; set; }
    }

    /// <summary>
    /// Paginated response for auto-reports
    /// </summary>
    public class AutoReportPagedResponse
    {
        public List<AutoReportItemDto> Items { get; set; } = new();
        public int TotalCount { get; set; }
        public int Page { get; set; }
        public int PageSize { get; set; }
        public int TotalPages => (int)Math.Ceiling((double)TotalCount / PageSize);
        public bool HasPrevious => Page > 1;
        public bool HasNext => Page < TotalPages;
    }
}
