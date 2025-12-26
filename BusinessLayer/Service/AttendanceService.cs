using BusinessLayer.DTOs.Attendance;
using BusinessLayer.Service.Interface;
using DataLayer.Entities;
using DataLayer.Enum;
using DataLayer.Repositories.Abstraction;
using DataLayer.Repositories.Abstraction.Schedule;
using Microsoft.EntityFrameworkCore;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace BusinessLayer.Service
{
    public class AttendanceService : IAttendanceService
    {
        private readonly IUnitOfWork _uow;
        private readonly IScheduleUnitOfWork _scheduleUow;
        private readonly INotificationService _notificationService;

        public AttendanceService(IUnitOfWork uow, IScheduleUnitOfWork scheduleUow, INotificationService notificationService)
        {
            _uow = uow;
            _scheduleUow = scheduleUow;
            _notificationService = notificationService;
        }

        public async Task<AttendanceRecordDto> MarkAsync(string tutorUserId, MarkAttendanceRequest req)
        {
            // 1) Validate lesson + quyền tutor
            var lesson = await _uow.Attendances.GetLessonWithTutorDataAsync(req.LessonId)
                ?? throw new KeyNotFoundException("Không tìm thấy buổi học");

            // owningTutorId là TutorProfile.Id, không phải UserId
            // Cần lấy Tutor.UserId để so sánh
            var owningTutorUserId = lesson.Class?.Tutor?.UserId
                                    ?? lesson.ScheduleEntries.FirstOrDefault()?.Tutor?.UserId;
            if (string.IsNullOrEmpty(owningTutorUserId))
                throw new InvalidOperationException("Buổi học không gắn với gia sư");

            if (owningTutorUserId != tutorUserId)
                throw new UnauthorizedAccessException("Bạn không có quyền điểm danh buổi này");

            // 2) Validate student
            var student = await _uow.StudentProfiles.GetByIdAsync(req.StudentId)
                          ?? throw new KeyNotFoundException("Không tìm thấy học sinh");

            if (lesson.ClassId != null)
            {
                var studentIds = await _uow.Attendances.GetStudentIdsInClassAsync(lesson.ClassId);
                if (!studentIds.Contains(student.Id))
                    throw new InvalidOperationException("Học sinh không thuộc lớp");
            }
            // Với 1-1, nếu bạn muốn ràng buộc Lesson–Student thì thêm validate tại đây.

            // 3) Parse status
            if (!Enum.TryParse<AttendanceStatus>(req.Status, true, out var status))
                throw new ArgumentException("Trạng thái không hợp lệ");

            // 4) Upsert attendance
            var att = await _uow.Attendances.FindAsync(req.LessonId, req.StudentId);
            if (att == null)
            {
                att = new Attendance
                {
                    LessonId = req.LessonId,
                    StudentId = req.StudentId,
                    Status = status,
                    Notes = req.Notes,
                    CreatedAt = DateTime.Now,
                    UpdatedAt = DateTime.Now
                };
                await _uow.Attendances.CreateAsync(att);
            }
            else
            {
                att.Status = status;
                att.Notes = req.Notes;
                att.UpdatedAt = DateTime.Now;
                await _uow.Attendances.UpdateAsync(att);
            }

            await _uow.SaveChangesAsync();

            // Tự động chuyển Lesson.Status = COMPLETED nếu đã điểm danh đủ học sinh
            if (lesson.ClassId != null)
            {
                await TryCompleteLessonAsync(lesson.ClassId, req.LessonId);
            }

            // Gửi notification cho student khi attendance được mark
            if (!string.IsNullOrEmpty(student.UserId))
            {
                try
                {
                    var statusText = status switch
                    {
                        AttendanceStatus.Present => "Có mặt",
                        AttendanceStatus.Late => "Đi muộn",
                        AttendanceStatus.Absent => "Vắng mặt",
                        AttendanceStatus.Excused => "Có phép",
                        _ => "Đã điểm danh"
                    };
                    var notification = await _notificationService.CreateAccountNotificationAsync(
                        student.UserId,
                        NotificationType.AttendanceMarked,
                        $"Điểm danh: {statusText}.{(string.IsNullOrWhiteSpace(req.Notes) ? "" : $" Ghi chú: {req.Notes}")}",
                        req.LessonId);
                    await _uow.SaveChangesAsync();
                    await _notificationService.SendRealTimeNotificationAsync(student.UserId, notification);
                }
                catch (Exception notifEx)
                {
                    System.Diagnostics.Debug.WriteLine($"Failed to send notification: {notifEx.Message}");
                }
            }

            var studentUser = await _uow.Users.GetByIdAsync(student.UserId!);
            return new AttendanceRecordDto
            {
                LessonId = att.LessonId,
                StudentId = att.StudentId,
                StudentName = studentUser?.UserName ?? "Student",
                Status = att.Status.ToString(),
                Notes = att.Notes,
                CreatedAt = att.CreatedAt,
                UpdatedAt = att.UpdatedAt
            };
        }

        public async Task<List<AttendanceRecordDto>> BulkMarkForLessonAsync(
            string tutorUserId, string lessonId,
            Dictionary<string, string> studentStatusMap, string? notes = null)
        {
            // Load lesson & quyền
            var lesson = await _uow.Attendances.GetLessonWithTutorDataAsync(lessonId)
                ?? throw new KeyNotFoundException("Không tìm thấy buổi học");

            var owningTutorUserId = lesson.Class?.Tutor?.UserId
                                    ?? lesson.ScheduleEntries.FirstOrDefault()?.Tutor?.UserId;
            if (string.IsNullOrEmpty(owningTutorUserId) || owningTutorUserId != tutorUserId)
                throw new UnauthorizedAccessException("Bạn không có quyền điểm danh buổi này");

            // DS học sinh thuộc lớp (nếu là lớp)
            HashSet<string>? classStudentIds = null;
            if (lesson.ClassId != null)
            {
                var studentIds = await _uow.Attendances.GetStudentIdsInClassAsync(lesson.ClassId);
                classStudentIds = studentIds.ToHashSet();
            }

            var result = new List<AttendanceRecordDto>();

            foreach (var kv in studentStatusMap)
            {
                var stuId = kv.Key;
                var statusText = kv.Value;

                if (lesson.ClassId != null && (classStudentIds == null || !classStudentIds.Contains(stuId)))
                    continue; // bỏ qua HS không thuộc lớp

                if (!Enum.TryParse<AttendanceStatus>(statusText, true, out var status))
                    continue;

                var att = await _uow.Attendances.FindAsync(lessonId, stuId);
                if (att == null)
                {
                    att = new Attendance
                    {
                        LessonId = lessonId,
                        StudentId = stuId,
                        Status = status,
                        Notes = notes,
                        CreatedAt = DateTime.Now,
                        UpdatedAt = DateTime.Now
                    };
                    await _uow.Attendances.CreateAsync(att);
                }
                else
                {
                    att.Status = status;
                    att.Notes = notes;
                    att.UpdatedAt = DateTime.Now;
                    await _uow.Attendances.UpdateAsync(att);
                }
                result.Add(new AttendanceRecordDto
                {
                    LessonId = lessonId,
                    StudentId = stuId,
                    StudentName = "", // sẽ fill sau
                    Status = status.ToString(),
                    Notes = notes ?? att.Notes,
                    CreatedAt = att.CreatedAt,
                    UpdatedAt = att.UpdatedAt
                });
            }

            await _uow.SaveChangesAsync();

            // Tự động chuyển Lesson.Status = COMPLETED nếu đã điểm danh đủ học sinh
            if (lesson.ClassId != null)
            {
                await TryCompleteLessonAsync(lesson.ClassId, lessonId);
            }

            // Gửi notification cho từng student khi attendance được mark
            if (result.Count > 0)
            {
                var stuIds = result.Select(r => r.StudentId).Distinct().ToList();
                var studentProfilesQuery = await _uow.StudentProfiles.GetAllAsync(
                    filter: s => stuIds.Contains(s.Id),
                    includes: q => q.Include(s => s.User)
                );
                var studentProfiles = studentProfilesQuery.ToList();

                foreach (var studentProfile in studentProfiles)
                {
                    if (!string.IsNullOrEmpty(studentProfile.UserId))
                    {
                        try
                        {
                            var studentResult = result.FirstOrDefault(r => r.StudentId == studentProfile.Id);
                            if (studentResult != null)
                            {
                                var statusText = studentResult.Status switch
                                {
                                    "Present" => "Có mặt",
                                    "Late" => "Đi muộn",
                                    "Absent" => "Vắng mặt",
                                    "Excused" => "Có phép",
                                    _ => "Đã điểm danh"
                                };
                                var notification = await _notificationService.CreateAccountNotificationAsync(
                                    studentProfile.UserId,
                                    NotificationType.AttendanceMarked,
                                    $"Điểm danh: {statusText}.{(string.IsNullOrWhiteSpace(studentResult.Notes) ? "" : $" Ghi chú: {studentResult.Notes}")}",
                                    lessonId);
                                await _uow.SaveChangesAsync();
                                await _notificationService.SendRealTimeNotificationAsync(studentProfile.UserId, notification);
                            }
                        }
                        catch (Exception notifEx)
                        {
                            System.Diagnostics.Debug.WriteLine($"Failed to send notification: {notifEx.Message}");
                        }
                    }
                }

                // Fill tên HS
                var users = studentProfiles.ToDictionary(s => s.Id, s => s.User?.UserName ?? "Student");
                foreach (var r in result)
                {
                    if (users.TryGetValue(r.StudentId, out var name))
                        r.StudentName = name;
                }
            }

            return result;
        }

        public async Task<IEnumerable<AttendanceRecordDto>> GetLessonAttendanceAsync(string tutorUserId, string lessonId)
        {
            // quyền
            var lesson = await _uow.Attendances.GetLessonWithTutorDataAsync(lessonId)
                ?? throw new KeyNotFoundException("Không tìm thấy buổi học");

            var owningTutorUserId = lesson.Class?.Tutor?.UserId
                                    ?? lesson.ScheduleEntries.FirstOrDefault()?.Tutor?.UserId;
            if (owningTutorUserId != tutorUserId)
                throw new UnauthorizedAccessException("Bạn không có quyền xem danh sách điểm danh này");

            var atts = await _uow.Attendances.GetByLessonAsync(lessonId);

            var rs = new List<AttendanceRecordDto>();
            foreach (var a in atts)
            {
                var name = a.Student?.User?.UserName ?? "Student";
                rs.Add(new AttendanceRecordDto
                {
                    LessonId = a.LessonId,
                    StudentId = a.StudentId,
                    StudentName = name,
                    Status = a.Status.ToString(),
                    Notes = a.Notes,
                    CreatedAt = a.CreatedAt,
                    UpdatedAt = a.UpdatedAt
                });
            }
            return rs;
        }

        public async Task<IEnumerable<ScheduleCellAttendanceDto>> GetTutorAttendanceOnCalendarAsync(
            string tutorUserId, DateTime start, DateTime end)
        {
            var startUtc = start.Date.ToUniversalTime();
            var endUtc = end.Date.AddDays(1).ToUniversalTime();

            // Lấy TutorProfile.Id từ UserId
            var tutorProfile = await _uow.TutorProfiles.GetByUserIdAsync(tutorUserId)
                ?? throw new KeyNotFoundException("Không tìm thấy thông tin gia sư");

            // Lấy schedule entries thuộc tutor trong range
            var entriesQuery = await _scheduleUow.ScheduleEntries.GetAllAsync(
                filter: se => se.TutorId == tutorProfile.Id
                          && se.EntryType == EntryType.LESSON
                          && se.DeletedAt == null
                          && se.StartTime < endUtc
                          && se.EndTime > startUtc,
                includes: q => q.Include(se => se.Lesson)
                                .OrderBy(se => se.StartTime)
            );
            var entries = entriesQuery.ToList();

            var lessonIds = entries.Where(e => e.LessonId != null).Select(e => e.LessonId!).Distinct().ToList();

            var atts = await _uow.Attendances.GetAttendancesByLessonIdsAsync(lessonIds);

            // Với lớp: nếu muốn biết tổng sĩ số chuẩn, load ClassAssign; ở đây dùng số bản ghi attendance hiện có
            var list = new List<ScheduleCellAttendanceDto>();
            foreach (var e in entries)
            {
                if (e.LessonId == null) continue;

                var group = atts.Where(a => a.LessonId == e.LessonId);
                var dto = new ScheduleCellAttendanceDto
                {
                    ScheduleEntryId = e.Id,
                    LessonId = e.LessonId,
                    StartTime = e.StartTime,
                    EndTime = e.EndTime,
                    TotalStudents = group.Count(),
                    Present = group.Count(x => x.Status == AttendanceStatus.Present),
                    Late = group.Count(x => x.Status == AttendanceStatus.Late),
                    Absent = group.Count(x => x.Status == AttendanceStatus.Absent),
                    Excused = group.Count(x => x.Status == AttendanceStatus.Excused)
                };
                list.Add(dto);
            }
            return list;
        }

        public async Task<IEnumerable<ScheduleCellAttendanceDto>> GetStudentAttendanceOnCalendarAsync(
            string studentUserId, DateTime start, DateTime end)
        {
            var startUtc = start.Date.ToUniversalTime();
            var endUtc = end.Date.AddDays(1).ToUniversalTime();

            // Tìm student profile từ userId
            var stu = await _uow.StudentProfiles.GetAsync(s => s.UserId == studentUserId)
                      ?? throw new KeyNotFoundException("không tìm thấy student profile");

            // Entry thuộc lesson có attendance của HS này
            var entriesQuery = await _scheduleUow.ScheduleEntries.GetAllAsync(
                filter: se => se.EntryType == EntryType.LESSON
                          && se.DeletedAt == null
                          && se.StartTime < endUtc
                          && se.EndTime > startUtc,
                includes: q => q.Include(se => se.Lesson)
                                    .ThenInclude(l => l.Class)
                                        .ThenInclude(c => c.ClassAssigns)
            );
            var entries = entriesQuery
                .Where(se => se.Lesson != null
                          && se.Lesson.Class != null
                          && se.Lesson.Class.ClassAssigns.Any(ca => ca.StudentId == stu.Id))
                .ToList();

            var lessonIds = entries.Where(e => e.LessonId != null).Select(e => e.LessonId!).ToList();

            var atts = await _uow.Attendances.GetAttendancesByLessonIdsAsync(lessonIds);

            // Overlay mỗi entry của HS (1-1 sẽ có 1 record/lesson)
            var list = new List<ScheduleCellAttendanceDto>();
            foreach (var e in entries)
            {
                if (e.LessonId == null) continue;
                var a = atts.FirstOrDefault(x => x.LessonId == e.LessonId);
                if (a == null) continue; // chưa có bản ghi -> ko hiện hoặc bạn có thể show Total=0

                list.Add(new ScheduleCellAttendanceDto
                {
                    ScheduleEntryId = e.Id,
                    LessonId = e.LessonId,
                    StartTime = e.StartTime,
                    EndTime = e.EndTime,
                    TotalStudents = 1,
                    Present = a.Status == AttendanceStatus.Present ? 1 : 0,
                    Late = a.Status == AttendanceStatus.Late ? 1 : 0,
                    Absent = a.Status == AttendanceStatus.Absent ? 1 : 0,
                    Excused = a.Status == AttendanceStatus.Excused ? 1 : 0
                });
            }
            return list.OrderBy(x => x.StartTime);
        }

        public async Task<IEnumerable<ScheduleCellAttendanceDto>> GetParentChildAttendanceCalendarAsync(
            string parentUserId, string studentId, DateTime start, DateTime end)
        {
            // Xác minh parent có link với studentId
            var link = await _uow.ParentProfiles.GetLinkAsync(parentUserId, studentId);
            if (link == null) throw new UnauthorizedAccessException("không có quyền xem học sinh này");

            var stu = await _uow.StudentProfiles.GetByIdAsync(studentId)
                      ?? throw new KeyNotFoundException("không tìm thấy học sinh");

            var startUtc = start.Date.ToUniversalTime();
            var endUtc = end.Date.AddDays(1).ToUniversalTime();

            var entriesQuery = await _scheduleUow.ScheduleEntries.GetAllAsync(
                filter: se => se.EntryType == EntryType.LESSON
                          && se.DeletedAt == null
                          && se.StartTime < endUtc
                          && se.EndTime > startUtc,
                includes: q => q.Include(se => se.Lesson)
                                    .ThenInclude(l => l.Class)
                                        .ThenInclude(c => c.ClassAssigns)
            );
            var entries = entriesQuery
                .Where(se => se.Lesson != null
                          && se.Lesson.Class != null
                          && se.Lesson.Class.ClassAssigns.Any(ca => ca.StudentId == stu.Id))
                .ToList();

            var lessonIds = entries.Where(e => e.LessonId != null).Select(e => e.LessonId!).ToList();

            var atts = await _uow.Attendances.GetAttendancesByLessonIdsAsync(lessonIds);

            var list = new List<ScheduleCellAttendanceDto>();
            foreach (var e in entries)
            {
                if (e.LessonId == null) continue;
                var a = atts.FirstOrDefault(x => x.LessonId == e.LessonId);
                if (a == null) continue;

                list.Add(new ScheduleCellAttendanceDto
                {
                    ScheduleEntryId = e.Id,
                    LessonId = e.LessonId,
                    StartTime = e.StartTime,
                    EndTime = e.EndTime,
                    TotalStudents = 1,
                    Present = a.Status == AttendanceStatus.Present ? 1 : 0,
                    Late = a.Status == AttendanceStatus.Late ? 1 : 0,
                    Absent = a.Status == AttendanceStatus.Absent ? 1 : 0,
                    Excused = a.Status == AttendanceStatus.Excused ? 1 : 0
                });
            }
            return list.OrderBy(x => x.StartTime);
        }

        public async Task<List<LessonRosterItemDto>> GetLessonRosterForTutorAsync(string tutorUserId, string lessonId)
        {
            var lesson = await _uow.Attendances.GetLessonWithTutorDataAsync(lessonId)
                ?? throw new KeyNotFoundException("Không tìm thấy buổi học");

            var owningTutorUserId = lesson.Class?.Tutor?.UserId
                                    ?? lesson.ScheduleEntries.FirstOrDefault()?.Tutor?.UserId;
            if (string.IsNullOrEmpty(owningTutorUserId) || owningTutorUserId != tutorUserId)
                throw new UnauthorizedAccessException("Bạn không có quyền xem danh sách học sinh của buổi này");

            if (lesson.ClassId == null)
                throw new InvalidOperationException("Buổi học 1-1 không có danh sách, vui lòng điểm danh trực tiếp");

            // DS học sinh đã đăng ký lớp
            var assigns = await _uow.Attendances.GetClassAssignsWithStudentsAsync(lesson.ClassId);

            var studentIds = assigns.Where(x => x.StudentId != null)
                                    .Select(x => x.StudentId!)
                                    .ToList();

            // Attendance hiện có cho lesson này (để biết status hiện tại)
            var atts = await _uow.Attendances.GetAttendanceStatusMapAsync(lessonId, studentIds);

            var roster = assigns.Select(a => new LessonRosterItemDto
            {
                StudentId = a.StudentId!,
                StudentUserId = a.Student!.UserId!,
                StudentName = a.Student!.User?.UserName ?? "Học sinh",
                Email = a.Student!.User?.Email ?? "",
                AvatarUrl = a.Student!.User?.AvatarUrl,
                CurrentStatus = atts.TryGetValue(a.StudentId!, out var st) ? st : null
            }).OrderBy(x => x.StudentName).ToList();

            return roster;
        }

        // ========== NEW: Class-Based Attendance Methods ==========

        public async Task<ClassAttendanceOverviewDto> GetClassAttendanceOverviewAsync(string tutorUserId, string classId)
        {
            // 1. Validate tutor owns class
            var cls = await _uow.Classes.GetByIdAsync(classId)
                ?? throw new KeyNotFoundException("Không tìm thấy lớp học");

            var tutorProfile = await _uow.TutorProfiles.GetByUserIdAsync(tutorUserId)
                ?? throw new KeyNotFoundException("Không tìm thấy thông tin gia sư");

            if (cls.TutorId != tutorProfile.Id)
                throw new UnauthorizedAccessException("Bạn không có quyền xem lớp này");

            // 2. Get all approved students in class
            var studentAssigns = await _uow.Attendances.GetClassAssignsWithStudentsAsync(classId);
            var approvedStudents = studentAssigns
                .Where(a => a.ApprovalStatus == ApprovalStatus.Approved && a.StudentId != null)
                .Select(a => a.Student!)
                .ToList();

            // 3. Get all lessons in class (with schedule entries for time info)
            var lessonList = (await _scheduleUow.Lessons.GetByClassWithScheduleEntriesAsync(classId)).ToList();

            // 4. Get all attendance records for this class
            var lessonIds = lessonList.Select(l => l.Id).ToList();
            var allAttendances = await _uow.Attendances.GetAttendancesByLessonIdsAsync(lessonIds);

            // 5. Build Tab 1 data: Students summary
            var studentsSummary = approvedStudents.Select(student =>
            {
                var studentAttendances = allAttendances.Where(a => a.StudentId == student.Id).ToList();
                var totalLessons = lessonList.Count;
                var presentCount = studentAttendances.Count(a => a.Status == AttendanceStatus.Present);
                var lateCount = studentAttendances.Count(a => a.Status == AttendanceStatus.Late);
                var absentCount = studentAttendances.Count(a => a.Status == AttendanceStatus.Absent);
                var excusedCount = studentAttendances.Count(a => a.Status == AttendanceStatus.Excused);
                var markedCount = studentAttendances.Count;
                var attendanceRate = totalLessons > 0 ? (double)presentCount / totalLessons * 100 : 0;

                return new StudentAttendanceSummaryDto
                {
                    StudentId = student.Id,
                    StudentUserId = student.UserId!,
                    StudentName = student.User?.UserName ?? "Student",
                    Email = student.User?.Email,
                    AvatarUrl = student.User?.AvatarUrl,
                    TotalLessons = totalLessons,
                    PresentCount = presentCount,
                    LateCount = lateCount,
                    AbsentCount = absentCount,
                    ExcusedCount = excusedCount,
                    NotMarkedCount = totalLessons - markedCount,
                    AttendanceRate = Math.Round(attendanceRate, 2)
                };
            }).OrderBy(s => s.StudentName).ToList();

            // 6. Build Tab 2 data: Lessons summary
            var lessonsSummary = lessonList.Select((lesson, index) =>
            {
                var lessonAttendances = allAttendances.Where(a => a.LessonId == lesson.Id).ToList();
                var totalStudents = approvedStudents.Count;
                var presentCount = lessonAttendances.Count(a => a.Status == AttendanceStatus.Present);
                var lateCount = lessonAttendances.Count(a => a.Status == AttendanceStatus.Late);
                var absentCount = lessonAttendances.Count(a => a.Status == AttendanceStatus.Absent);
                var excusedCount = lessonAttendances.Count(a => a.Status == AttendanceStatus.Excused);
                var markedCount = lessonAttendances.Count;
                var attendanceRate = totalStudents > 0 ? (double)presentCount / totalStudents * 100 : 0;

                var scheduleEntry = lesson.ScheduleEntries?.FirstOrDefault();

                return new LessonAttendanceSummaryDto
                {
                    LessonId = lesson.Id,
                    LessonNumber = index + 1,
                    LessonDate = scheduleEntry?.StartTime.Date ?? lesson.CreatedAt.Date,
                    StartTime = scheduleEntry?.StartTime ?? lesson.CreatedAt,
                    EndTime = scheduleEntry?.EndTime ?? lesson.CreatedAt.AddHours(2),
                    TotalStudents = totalStudents,
                    PresentCount = presentCount,
                    LateCount = lateCount,
                    AbsentCount = absentCount,
                    ExcusedCount = excusedCount,
                    NotMarkedCount = totalStudents - markedCount,
                    AttendanceRate = Math.Round(attendanceRate, 2)
                };
            }).ToList();

            return new ClassAttendanceOverviewDto
            {
                ClassId = classId,
                ClassName = cls.Title ?? "Lớp học",
                Subject = cls.Subject,
                TotalStudents = approvedStudents.Count,
                TotalLessons = lessonList.Count,
                Students = studentsSummary,
                Lessons = lessonsSummary
            };
        }

        public async Task<StudentAttendanceDetailDto> GetStudentAttendanceInClassAsync(
            string tutorUserId, string classId, string studentId)
        {
            // 1. Validate tutor owns class
            var cls = await _uow.Classes.GetByIdAsync(classId)
                ?? throw new KeyNotFoundException("Không tìm thấy lớp học");

            var tutorProfile = await _uow.TutorProfiles.GetByUserIdAsync(tutorUserId)
                ?? throw new KeyNotFoundException("Không tìm thấy thông tin gia sư");

            if (cls.TutorId != tutorProfile.Id)
                throw new UnauthorizedAccessException("Bạn không có quyền xem lớp này");

            // 2. Validate student belongs to class
            var studentAssign = await _uow.ClassAssigns.GetAsync(ca => 
                ca.ClassId == classId && ca.StudentId == studentId && ca.ApprovalStatus == ApprovalStatus.Approved);
            if (studentAssign == null)
                throw new InvalidOperationException("Học sinh không thuộc lớp này");

            // 3. Get student info (with User for email/avatar)
            var student = await _uow.StudentProfiles.GetByIdWithUserAsync(studentId)
                ?? throw new KeyNotFoundException("Không tìm thấy học sinh");

            // 4. Get all lessons in class
            var lessonList = (await _scheduleUow.Lessons.GetByClassWithScheduleEntriesAsync(classId)).ToList();

            // 5. Get attendance records for this student
            var lessonIds = lessonList.Select(l => l.Id).ToList();
            var attendances = await _uow.Attendances.GetAttendancesByLessonIdsAsync(lessonIds);
            var studentAttendances = attendances.Where(a => a.StudentId == studentId).ToDictionary(a => a.LessonId);

            // 6. Build lesson details
            var lessonRecords = lessonList.Select((lesson, index) =>
            {
                var scheduleEntry = lesson.ScheduleEntries?.FirstOrDefault();
                studentAttendances.TryGetValue(lesson.Id, out var attendance);

                return new LessonAttendanceRecordDto
                {
                    LessonId = lesson.Id,
                    LessonNumber = index + 1,
                    LessonDate = scheduleEntry?.StartTime.Date ?? lesson.CreatedAt.Date,
                    StartTime = scheduleEntry?.StartTime ?? lesson.CreatedAt,
                    EndTime = scheduleEntry?.EndTime ?? lesson.CreatedAt.AddHours(2),
                    Status = attendance?.Status.ToString(),
                    Notes = attendance?.Notes
                };
            }).ToList();

            // 7. Calculate summary
            var totalLessons = lessonList.Count;
            var presentCount = studentAttendances.Values.Count(a => a.Status == AttendanceStatus.Present);
            var lateCount = studentAttendances.Values.Count(a => a.Status == AttendanceStatus.Late);
            var absentCount = studentAttendances.Values.Count(a => a.Status == AttendanceStatus.Absent);
            var excusedCount = studentAttendances.Values.Count(a => a.Status == AttendanceStatus.Excused);
            var attendanceRate = totalLessons > 0 ? (double)presentCount / totalLessons * 100 : 0;

            return new StudentAttendanceDetailDto
            {
                StudentId = studentId,
                StudentUserId = student.UserId!,
                StudentName = student.User?.UserName ?? "Student",
                Email = student.User?.Email,
                AvatarUrl = student.User?.AvatarUrl,
                ClassId = classId,
                ClassName = cls.Title ?? "Lớp học",
                TotalLessons = totalLessons,
                PresentCount = presentCount,
                LateCount = lateCount,
                AbsentCount = absentCount,
                ExcusedCount = excusedCount,
                AttendanceRate = Math.Round(attendanceRate, 2),
                Lessons = lessonRecords
            };
        }

        public async Task<LessonAttendanceDetailDto> GetLessonAttendanceDetailAsync(
            string tutorUserId, string lessonId)
        {
            // 1. Get lesson with class info
            var lessonEntity = await _scheduleUow.Lessons.GetByIdWithClassAndScheduleAsync(lessonId)
                ?? throw new KeyNotFoundException("Không tìm thấy buổi học");

            if (lessonEntity.ClassId == null)
                throw new InvalidOperationException("Buổi học không thuộc lớp nào");

            // 2. Validate tutor owns class
            var tutorProfile = await _uow.TutorProfiles.GetByUserIdAsync(tutorUserId)
                ?? throw new KeyNotFoundException("Không tìm thấy thông tin gia sư");

            if (lessonEntity.Class?.TutorId != tutorProfile.Id)
                throw new UnauthorizedAccessException("Bạn không có quyền xem buổi học này");

            // 3. Get all students in class
            var studentAssigns = await _uow.Attendances.GetClassAssignsWithStudentsAsync(lessonEntity.ClassId);
            var approvedStudents = studentAssigns
                .Where(a => a.ApprovalStatus == ApprovalStatus.Approved && a.StudentId != null)
                .ToList();

            // 4. Get attendance for this lesson
            var attendances = await _uow.Attendances.GetByLessonAsync(lessonId);
            var attendanceDict = attendances.ToDictionary(a => a.StudentId);

            // 5. Build student list
            var studentRecords = approvedStudents.Select(assign =>
            {
                attendanceDict.TryGetValue(assign.StudentId!, out var attendance);

                return new StudentAttendanceInLessonDto
                {
                    StudentId = assign.StudentId!,
                    StudentUserId = assign.Student!.UserId!,
                    StudentName = assign.Student!.User?.UserName ?? "Student",
                    Email = assign.Student!.User?.Email,
                    AvatarUrl = assign.Student!.User?.AvatarUrl,
                    Status = attendance?.Status.ToString(),
                    Notes = attendance?.Notes
                };
            }).OrderBy(s => s.StudentName).ToList();

            // 6. Calculate summary
            var totalStudents = approvedStudents.Count;
            var presentCount = attendances.Count(a => a.Status == AttendanceStatus.Present);
            var lateCount = attendances.Count(a => a.Status == AttendanceStatus.Late);
            var absentCount = attendances.Count(a => a.Status == AttendanceStatus.Absent);
            var excusedCount = attendances.Count(a => a.Status == AttendanceStatus.Excused);
            var attendanceRate = totalStudents > 0 ? (double)presentCount / totalStudents * 100 : 0;

            // 7. Get lesson number from all lessons in class
            var allLessons = await _scheduleUow.Lessons.GetByClassWithScheduleEntriesAsync(lessonEntity.ClassId);
            var lessonNumber = allLessons.ToList().FindIndex(l => l.Id == lessonId) + 1;
            var scheduleEntry = lessonEntity.ScheduleEntries?.FirstOrDefault();

            return new LessonAttendanceDetailDto
            {
                LessonId = lessonId,
                LessonNumber = lessonNumber,
                LessonDate = scheduleEntry?.StartTime.Date ?? lessonEntity.CreatedAt.Date,
                StartTime = scheduleEntry?.StartTime ?? lessonEntity.CreatedAt,
                EndTime = scheduleEntry?.EndTime ?? lessonEntity.CreatedAt.AddHours(2),
                ClassId = lessonEntity.ClassId,
                ClassName = lessonEntity.Class?.Title ?? "Lớp học",
                TotalStudents = totalStudents,
                PresentCount = presentCount,
                LateCount = lateCount,
                AbsentCount = absentCount,
                ExcusedCount = excusedCount,
                AttendanceRate = Math.Round(attendanceRate, 2),
                Students = studentRecords
            };
        }

        public async Task<StudentAttendanceDetailDto> GetMyClassAttendanceAsync(
            string studentUserId, string classId)
        {
            // 1. Get student profile (with User for email/avatar)
            var student = await _uow.StudentProfiles.GetByUserIdWithUserAsync(studentUserId)
                ?? throw new KeyNotFoundException("Không tìm thấy thông tin học sinh");

            // 2. Validate student belongs to class
            var studentAssign = await _uow.ClassAssigns.GetAsync(ca => 
                ca.ClassId == classId && ca.StudentId == student.Id && ca.ApprovalStatus == ApprovalStatus.Approved);
            if (studentAssign == null)
                throw new UnauthorizedAccessException("Bạn không thuộc lớp này");

            // 3. Get class info
            var cls = await _uow.Classes.GetByIdAsync(classId)
                ?? throw new KeyNotFoundException("Không tìm thấy lớp học");

            // 4. Get all lessons
            var lessonList = (await _scheduleUow.Lessons.GetByClassWithScheduleEntriesAsync(classId)).ToList();

            // 5. Get attendance records
            var lessonIds = lessonList.Select(l => l.Id).ToList();
            var attendances = await _uow.Attendances.GetAttendancesByLessonIdsAsync(lessonIds);
            var myAttendances = attendances.Where(a => a.StudentId == student.Id).ToDictionary(a => a.LessonId);

            // 6. Build lessons with attendance
            var lessonRecords = lessonList.Select((lesson, index) =>
            {
                var scheduleEntry = lesson.ScheduleEntries?.FirstOrDefault();
                myAttendances.TryGetValue(lesson.Id, out var attendance);

                return new LessonAttendanceRecordDto
                {
                    LessonId = lesson.Id,
                    LessonNumber = index + 1,
                    LessonDate = scheduleEntry?.StartTime.Date ?? lesson.CreatedAt.Date,
                    StartTime = scheduleEntry?.StartTime ?? lesson.CreatedAt,
                    EndTime = scheduleEntry?.EndTime ?? lesson.CreatedAt.AddHours(2),
                    Status = attendance?.Status.ToString(),
                    Notes = attendance?.Notes
                };
            }).ToList();

            // 7. Calculate summary
            var totalLessons = lessonList.Count;
            var presentCount = myAttendances.Values.Count(a => a.Status == AttendanceStatus.Present);
            var lateCount = myAttendances.Values.Count(a => a.Status == AttendanceStatus.Late);
            var absentCount = myAttendances.Values.Count(a => a.Status == AttendanceStatus.Absent);
            var excusedCount = myAttendances.Values.Count(a => a.Status == AttendanceStatus.Excused);
            var attendanceRate = totalLessons > 0 ? (double)presentCount / totalLessons * 100 : 0;

            return new StudentAttendanceDetailDto
            {
                StudentId = student.Id,
                StudentUserId = studentUserId,
                StudentName = student.User?.UserName ?? "Student",
                Email = student.User?.Email,
                AvatarUrl = student.User?.AvatarUrl,
                ClassId = classId,
                ClassName = cls.Title ?? "Lớp học",
                TotalLessons = totalLessons,
                PresentCount = presentCount,
                LateCount = lateCount,
                AbsentCount = absentCount,
                ExcusedCount = excusedCount,
                AttendanceRate = Math.Round(attendanceRate, 2),
                Lessons = lessonRecords
            };
        }

        public async Task<StudentAttendanceDetailDto> GetChildClassAttendanceAsync(
            string parentUserId, string studentId, string classId)
        {
            // 1. Validate parent-child relationship
            var link = await _uow.ParentProfiles.GetLinkAsync(parentUserId, studentId);
            if (link == null)
                throw new UnauthorizedAccessException("Bạn không có quyền xem học sinh này");

            // 2. Get student (with User for email/avatar)
            var student = await _uow.StudentProfiles.GetByIdWithUserAsync(studentId)
                ?? throw new KeyNotFoundException("Không tìm thấy học sinh");

            // 3. Validate child belongs to class
            var studentAssign = await _uow.ClassAssigns.GetAsync(ca => 
                ca.ClassId == classId && ca.StudentId == studentId && ca.ApprovalStatus == ApprovalStatus.Approved);
            if (studentAssign == null)
                throw new InvalidOperationException("Con bạn không thuộc lớp này");

            // 4. Get class info
            var cls = await _uow.Classes.GetByIdAsync(classId)
                ?? throw new KeyNotFoundException("Không tìm thấy lớp học");

            // 5. Get lessons
            var lessonList = (await _scheduleUow.Lessons.GetByClassWithScheduleEntriesAsync(classId)).ToList();

            // 6. Get attendance
            var lessonIds = lessonList.Select(l => l.Id).ToList();
            var attendances = await _uow.Attendances.GetAttendancesByLessonIdsAsync(lessonIds);
            var childAttendances = attendances.Where(a => a.StudentId == studentId).ToDictionary(a => a.LessonId);

            // 7. Build lesson records
            var lessonRecords = lessonList.Select((lesson, index) =>
            {
                var scheduleEntry = lesson.ScheduleEntries?.FirstOrDefault();
                childAttendances.TryGetValue(lesson.Id, out var attendance);

                return new LessonAttendanceRecordDto
                {
                    LessonId = lesson.Id,
                    LessonNumber = index + 1,
                    LessonDate = scheduleEntry?.StartTime.Date ?? lesson.CreatedAt.Date,
                    StartTime = scheduleEntry?.StartTime ?? lesson.CreatedAt,
                    EndTime = scheduleEntry?.EndTime ?? lesson.CreatedAt.AddHours(2),
                    Status = attendance?.Status.ToString(),
                    Notes = attendance?.Notes
                };
            }).ToList();

            // 8. Calculate summary
            var totalLessons = lessonList.Count;
            var presentCount = childAttendances.Values.Count(a => a.Status == AttendanceStatus.Present);
            var lateCount = childAttendances.Values.Count(a => a.Status == AttendanceStatus.Late);
            var absentCount = childAttendances.Values.Count(a => a.Status == AttendanceStatus.Absent);
            var excusedCount = childAttendances.Values.Count(a => a.Status == AttendanceStatus.Excused);
            var attendanceRate = totalLessons > 0 ? (double)presentCount / totalLessons * 100 : 0;

            return new StudentAttendanceDetailDto
            {
                StudentId = studentId,
                StudentUserId = student.UserId!,
                StudentName = student.User?.UserName ?? "Student",
                Email = student.User?.Email,
                AvatarUrl = student.User?.AvatarUrl,
                ClassId = classId,
                ClassName = cls.Title ?? "Lớp học",
                TotalLessons = totalLessons,
                PresentCount = presentCount,
                LateCount = lateCount,
                AbsentCount = absentCount,
                ExcusedCount = excusedCount,
                AttendanceRate = Math.Round(attendanceRate, 2),
                Lessons = lessonRecords
            };
        }

        /// <summary>
        /// Tự động chuyển Lesson.Status = COMPLETED nếu đã điểm danh đủ học sinh trong lớp
        /// </summary>
        private async Task TryCompleteLessonAsync(string classId, string lessonId)
        {
            try
            {
                // Lấy lesson
                var lesson = await _scheduleUow.Lessons.GetByIdAsync(lessonId);
                if (lesson == null || lesson.Status == LessonStatus.COMPLETED)
                    return; // Đã completed rồi hoặc không tồn tại

                // Lấy danh sách học sinh trong lớp (có PaymentStatus = Paid)
                var studentsInClass = await _uow.ClassAssigns.GetAllAsync(
                    filter: ca => ca.ClassId == classId && ca.PaymentStatus == PaymentStatus.Paid
                );
                var studentIds = studentsInClass.Select(ca => ca.StudentId).ToList();

                if (!studentIds.Any())
                    return; // Không có học sinh nào

                // Đếm số học sinh đã được điểm danh (có Attendance record)
                var markedCount = await _uow.Attendances.GetAllAsync(
                    filter: a => a.LessonId == lessonId && studentIds.Contains(a.StudentId)
                );
                var markedStudentIds = markedCount.Select(a => a.StudentId).Distinct().ToList();

                // Nếu tất cả học sinh đã được điểm danh → chuyển Lesson.Status = COMPLETED
                if (markedStudentIds.Count == studentIds.Count)
                {
                    lesson.Status = LessonStatus.COMPLETED;
                    await _scheduleUow.Lessons.UpdateAsync(lesson);
                    await _scheduleUow.SaveChangesAsync();
                }
            }
            catch (Exception ex)
            {
                // Log lỗi nhưng không throw để không ảnh hưởng đến flow điểm danh
                System.Diagnostics.Debug.WriteLine($"TryCompleteLessonAsync error: {ex.Message}");
            }
        }
    }
}
