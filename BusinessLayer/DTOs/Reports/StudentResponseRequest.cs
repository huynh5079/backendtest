namespace BusinessLayer.DTOs.Reports
{
    public class StudentResponseRequest
    {
        public required string Token { get; set; }
        public required string Action { get; set; } // "continue" or "cancel"
    }
}
