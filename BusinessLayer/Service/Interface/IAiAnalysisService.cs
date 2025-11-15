using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace BusinessLayer.Service.Interface
{
    public interface IAiAnalysisService
    {
        /// <summary>
        /// Phân tích một file (từ URL) dựa trên một bối cảnh (context)
        /// </summary>
        /// <param name="contextTitle">Bối cảnh (vd: "Toán lớp 12 - Chủ đề Lũy thừa")</param>
        /// <param name="fileUrl">URL công khai của file (vd: Cloudinary URL)</param>
        /// <param name="mimeType">Loại file (vd: "application/pdf", "video/mp4")</param>
        /// <returns>Nội dung text do AI phân tích</returns>
        Task<string> AnalyzeFileRelevanceAsync(string contextTitle, string fileUrl, string mimeType);
    }
}
