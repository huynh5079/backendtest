using Microsoft.AspNetCore.Http;

namespace BusinessLayer.DTOs.Profile
{
    public class UpdateAvatarRequest
    {
        public IFormFile Avatar { get; set; } = null!;
    }
}
