namespace BusinessLayer.DTOs.Reports
{
    /// <summary>
    /// Query parameters for listing auto-reports with pagination
    /// </summary>
    public class AutoReportQuery
    {
        /// <summary>
        /// Page number (1-indexed)
        /// </summary>
        public int Page { get; set; } = 1;

        /// <summary>
        /// Number of items per page (max 100)
        /// </summary>
        public int PageSize { get; set; } = 10;

        /// <summary>
        /// Filter by class ID
        /// </summary>
        public string? ClassId { get; set; }

        /// <summary>
        /// Filter by student user ID
        /// </summary>
        public string? StudentId { get; set; }

        /// <summary>
        /// Filter by date from (inclusive)
        /// </summary>
        public DateTime? FromDate { get; set; }

        /// <summary>
        /// Filter by date to (inclusive)
        /// </summary>
        public DateTime? ToDate { get; set; }

        /// <summary>
        /// Filter by student response status
        /// </summary>
        public string? ResponseStatus { get; set; } // "responded", "pending", "all"

        /// <summary>
        /// Sort by field
        /// </summary>
        public string SortBy { get; set; } = "CreatedAt";

        /// <summary>
        /// Sort order: "asc" or "desc"
        /// </summary>
        public string SortOrder { get; set; } = "desc";
    }
}
