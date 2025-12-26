using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace BusinessLayer.DTOs.Schedule.TutorApplication
{
    public class AcceptRequestResponseDto
    {
        public string ClassId { get; set; } = string.Empty;
        public bool PaymentRequired { get; set; } // True: Redirect Pay, False: Redirect Detail
        public string Message { get; set; } = string.Empty;
        public string? StudentAddress { get; set; } // Địa chỉ học sinh (chỉ có khi offline)
    }
}
