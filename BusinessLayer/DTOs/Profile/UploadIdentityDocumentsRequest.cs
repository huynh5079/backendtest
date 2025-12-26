using Microsoft.AspNetCore.Http;
using System.Collections.Generic;

namespace BusinessLayer.DTOs.Profile
{
    public class UploadIdentityDocumentsRequest
    {
        public List<IFormFile> IdentityDocuments { get; set; } = new();
    }
}
