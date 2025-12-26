using BusinessLayer.Service.Interface.IScheduleService;
using BusinessLayer.Helper;
using DataLayer.Entities;
using DataLayer.Enum;
using Microsoft.EntityFrameworkCore;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace BusinessLayer.Service.ScheduleService
{
    public class ScheduleGenerationService : IScheduleGenerationService
    {
        private readonly TpeduContext _context;
        private class StandardScheduleRule
        {
            public DayOfWeek DayOfWeek { get; set; }
            public TimeSpan StartTime { get; set; }
            public TimeSpan EndTime { get; set; }
        }

        public ScheduleGenerationService(TpeduContext context)
        {
            _context = context;
        }

        /// <summary>
        /// Implement function from Interface (ClassRequest Stream)
        /// (Entity C# uses TimeOnly)
        /// </summary>
        public async Task<DateTime?> GenerateScheduleFromRequestAsync(string classId, string tutorId, DateTime startDate, IEnumerable<ClassRequestSchedule> scheduleRules)
        { 
            var standardRules = scheduleRules.Select(r => new StandardScheduleRule
            {
                DayOfWeek = (DayOfWeek)r.DayOfWeek,
                StartTime = r.StartTime, 
                EndTime = r.EndTime 
            });
            // Core logic function
            return await GenerateScheduleLogicAsync(classId, tutorId, startDate, standardRules);
        }

        /// <summary>
        /// Implement function from Interface (Class stream created by Tutor)
        /// (Entity C# uses TimeOnly)
        /// </summary>
        public async Task<DateTime?> GenerateScheduleFromClassAsync(string classId, string tutorId, DateTime startDate, IEnumerable<ClassSchedule> scheduleRules)
        {
            var standardRules = scheduleRules.Select(r => new StandardScheduleRule
            {
                DayOfWeek = (DayOfWeek)r.DayOfWeek,

                StartTime = r.StartTime, 
                EndTime = r.EndTime 
            });

            // Core logic function
            return await GenerateScheduleLogicAsync(classId, tutorId, startDate, standardRules);
        }

        /// <summary>
        /// Core logic: Find empty slot and AddRange to Context
        /// </summary>
        private async Task<DateTime?> GenerateScheduleLogicAsync(string classId, string tutorId, DateTime startDate, IEnumerable<StandardScheduleRule> rules)
        {
            int totalSlotsToFind = rules.Count() * 4;
            int searchDayLimit = 100;

            var foundSlots = await FindAvailableSlotsAsync(
                tutorId, startDate, rules, totalSlotsToFind, searchDayLimit
            );

            var newLessons = new List<Lesson>();
            var newScheduleEntries = new List<ScheduleEntry>();

            foreach (var slot in foundSlots)
            {
                var newLesson = new Lesson
                {
                    Id = Guid.NewGuid().ToString(),
                    ClassId = classId,
                    Status = LessonStatus.SCHEDULED,
                    // Convert UTC sang Vietnam time để hiển thị đúng ngày
                    Title = $"Buổi học {DateTimeHelper.ToVietnamTime(slot.StartTimeUtc).ToString("dd/MM/yyyy")}"
                };
                newLessons.Add(newLesson);

                var newEntry = new ScheduleEntry
                {
                    Id = Guid.NewGuid().ToString(),
                    TutorId = tutorId,
                    StartTime = slot.StartTimeUtc,
                    EndTime = slot.EndTimeUtc,
                    EntryType = EntryType.LESSON,
                    LessonId = newLesson.Id,
                    BlockId = null
                };
                newScheduleEntries.Add(newEntry);
            }

            // AddRange to Context (no SaveChanges)
            await _context.Lessons.AddRangeAsync(newLessons);
            await _context.ScheduleEntries.AddRangeAsync(newScheduleEntries);

            // Nếu không tìm được slot nào, return null
            if (!foundSlots.Any())
            {
                Console.WriteLine($"[ScheduleGenerationService] CẢNH BÁO: Không tìm được slot nào để tạo lịch cho lớp {classId}.");
                return null;
            }

            var maxEndDateUtc = foundSlots.Max(s => s.EndTimeUtc);

            return maxEndDateUtc;
        }

        /// <summary>
        /// Helper finds empty slots    
        /// /// </summary>
        private async Task<List<(DateTime StartTimeUtc, DateTime EndTimeUtc)>> FindAvailableSlotsAsync(
            string tutorId,
            DateTime searchStartDate,
            IEnumerable<StandardScheduleRule> rules,
            int totalSlotsToFind,
            int searchDayLimit)
        {
            var foundSlots = new List<(DateTime StartTimeUtc, DateTime EndTimeUtc)>();
            // searchStartDate là Vietnam time, lấy ngày để bắt đầu tìm
            var currentDate = searchStartDate.Date;
            var endDateLimitVietnam = currentDate.AddDays(searchDayLimit);
            
            // Convert sang UTC để so sánh với dữ liệu trong DB (DB lưu UTC)
            var searchStartDateUtc = DateTimeHelper.ToUtc(searchStartDate);
            var endDateLimitUtc = DateTimeHelper.ToUtc(endDateLimitVietnam);

            var rulesDict = rules.GroupBy(r => r.DayOfWeek)
                                 .ToDictionary(g => g.Key, g => g.Select(r => (r.StartTime, r.EndTime)).ToList());

            Console.WriteLine($"[FindAvailableSlotsAsync] Tìm slot cho gia sư {tutorId}:");
            Console.WriteLine($"  - StartDate (Vietnam): {searchStartDate:dd/MM/yyyy HH:mm:ss}");
            Console.WriteLine($"  - StartDate (UTC): {searchStartDateUtc:dd/MM/yyyy HH:mm:ss}");
            Console.WriteLine($"  - EndDateLimit (Vietnam): {endDateLimitVietnam:dd/MM/yyyy HH:mm:ss}");
            Console.WriteLine($"  - EndDateLimit (UTC): {endDateLimitUtc:dd/MM/yyyy HH:mm:ss}");
            Console.WriteLine($"  - TotalSlotsToFind: {totalSlotsToFind}");
            Console.WriteLine($"  - Số rules: {rules.Count()}");
            Console.WriteLine($"  - Rules: {string.Join(", ", rules.Select(r => $"{r.DayOfWeek} {r.StartTime}-{r.EndTime}"))}");

            // Query existingEntries từ DB - dữ liệu trong DB được lưu ở UTC (Kind = Unspecified)
            // Cần so sánh với UTC để đúng
            var existingEntries = await _context.ScheduleEntries
                .Where(se => se.TutorId == tutorId &&
                             se.DeletedAt == null &&
                             se.StartTime < endDateLimitUtc &&
                             se.EndTime > searchStartDateUtc)
                .Select(se => new { se.StartTime, se.EndTime })
                .ToListAsync();

            Console.WriteLine($"[FindAvailableSlotsAsync] Tìm thấy {existingEntries.Count} ScheduleEntry đã có của gia sư {tutorId}");

            // currentDate là Vietnam time, so sánh với endDateLimitVietnam
            while (foundSlots.Count < totalSlotsToFind && currentDate < endDateLimitVietnam)
            {
                if (rulesDict.TryGetValue(currentDate.DayOfWeek, out var timesInDay))
                {
                    foreach (var timeRule in timesInDay)
                    {
                        // currentDate đã là Vietnam time (UTC+7), cần convert sang UTC để lưu vào DB
                        // Dùng DateTimeHelper.ToUtc() thay vì .ToUniversalTime() để convert đúng
                        var potentialStartTimeVietnam = currentDate.Add(timeRule.StartTime);
                        var potentialEndTimeVietnam = currentDate.Add(timeRule.EndTime);
                        
                        // Convert từ Vietnam time sang UTC để lưu vào DB
                        var potentialStartTime = DateTimeHelper.ToUtc(potentialStartTimeVietnam);
                        var potentialEndTime = DateTimeHelper.ToUtc(potentialEndTimeVietnam);

                        // searchStartDateUtc đã được convert ở ngoài vòng lặp (dòng 135)
                        if (potentialStartTime >= searchStartDateUtc)
                        {
                            bool isConflict = existingEntries.Any(existing =>
                                potentialStartTime < existing.EndTime &&
                                potentialEndTime > existing.StartTime
                            );

                            if (!isConflict)
                            {
                                foundSlots.Add((potentialStartTime, potentialEndTime));
                                existingEntries.Add(new { StartTime = potentialStartTime, EndTime = potentialEndTime });
                                if (foundSlots.Count >= totalSlotsToFind) break;
                            }
                        }
                    }
                }

                if (foundSlots.Count >= totalSlotsToFind)
                    break;

                currentDate = currentDate.AddDays(1);
            }

            // Không throw exception nữa, chỉ log warning nếu không tìm đủ slot
            // Vẫn tạo những slot tìm được để đảm bảo lịch được sinh ra
            if (foundSlots.Count < totalSlotsToFind)
            {
                Console.WriteLine($"[FindAvailableSlotsAsync] CẢNH BÁO: Không thể tìm đủ {totalSlotsToFind} buổi học trống cho gia sư {tutorId} trong vòng {searchDayLimit} ngày tới. Chỉ tìm thấy {foundSlots.Count} buổi. Vẫn sẽ tạo {foundSlots.Count} buổi học.");
            }
            else
            {
                Console.WriteLine($"[FindAvailableSlotsAsync] Đã tìm đủ {foundSlots.Count} slot cho gia sư {tutorId}");
            }

            return foundSlots;
        }
    }
}
