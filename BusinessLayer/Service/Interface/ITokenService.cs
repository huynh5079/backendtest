using DataLayer.Entities;

namespace BusinessLayer.Service.Interface;

public interface ITokenService
{
    string CreateToken(User user);
    
    /// <summary>
    /// Generate JWT token for student response to auto-report
    /// </summary>
    string GenerateStudentResponseToken(string reportId, string studentUserId);
    
    /// <summary>
    /// Validate and decode student response token
    /// Returns (reportId, studentUserId) if valid, null if invalid/expired
    /// </summary>
    (string reportId, string studentUserId)? ValidateStudentResponseToken(string token);
}