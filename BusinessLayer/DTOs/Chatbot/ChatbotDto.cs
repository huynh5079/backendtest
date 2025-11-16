using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace BusinessLayer.DTOs.Chatbot
{
    public class ChatbotRequestDto
    {
        [Required(ErrorMessage = "Vui lòng nhập câu hỏi.")]
        public string Question { get; set; } = default!;
    }

    public class ChatbotResponseDto
    {
        public string Answer { get; set; } = default!;
    }
}
