namespace BusinessLayer.Options
{
    public class FrontendSettings
    {
        /// <summary>
        /// Base URL của frontend application (e.g., https://your-frontend.com)
        /// </summary>
        public string BaseUrl { get; set; } = "https://frontend-quac.vercel.app/";

        /// <summary>
        /// Path cho student response page (e.g., /student-response)
        /// </summary>
        public string StudentResponsePath { get; set; } = "/student-response";

        /// <summary>
        /// Get full URL for student response
        /// </summary>
        public string GetStudentResponseUrl() => $"{BaseUrl.TrimEnd('/')}{StudentResponsePath}";
    }
}
