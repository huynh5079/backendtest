namespace BusinessLayer.Options
{
    public class AutoReportSettings
    {
        /// <summary>
        /// Absence rate threshold (e.g., 0.3 = 30%)
        /// </summary>
        public decimal AbsenceRateThreshold { get; set; } = 0.3m;

        /// <summary>
        /// Consecutive absence threshold (e.g., 3 consecutive absences)
        /// </summary>
        public int ConsecutiveAbsenceThreshold { get; set; } = 3;

        /// <summary>
        /// Minimum lessons before checking (e.g., 5 lessons)
        /// </summary>
        public int MinLessonsBeforeCheck { get; set; } = 5;

        /// <summary>
        /// Duplicate check window in days (e.g., 7 days)
        /// </summary>
        public int DuplicateCheckWindowDays { get; set; } = 7;

        /// <summary>
        /// Enable/disable auto-report feature
        /// </summary>
        public bool EnableAutoReport { get; set; } = true;
    }
}
