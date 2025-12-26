using Microsoft.AspNetCore.Http;
using System.Collections.Generic;

namespace BusinessLayer.DTOs.Profile
{
    public class UploadCertificatesRequest
    {
        public List<IFormFile> Certificates { get; set; } = new();
    }
}
