using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace BusinessLayer.Helper
{
    public static class EducationLevelHelper
    {
        public static List<string> GetGradesByLevel(string teachingLevel)
        {
            if (string.IsNullOrEmpty(teachingLevel)) return new List<string>();

            // Standardize input for comparison
            return teachingLevel.Trim().ToLower() switch
            {
                "tiểu học" => new List<string> { "Lớp 1", "Lớp 2", "Lớp 3", "Lớp 4", "Lớp 5" },
                "trung học cơ sở" => new List<string> { "Lớp 6", "Lớp 7", "Lớp 8", "Lớp 9" },
                "trung học phổ thông" => new List<string> { "Lớp 10", "Lớp 11", "Lớp 12" },
                _ => new List<string>()
            };
        }
    }
}
