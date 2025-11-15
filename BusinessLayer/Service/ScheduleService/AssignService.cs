using BusinessLayer.DTOs.Schedule.Class;
using BusinessLayer.Service.Interface;
using BusinessLayer.Service.Interface.IScheduleService;
using DataLayer.Entities;
using DataLayer.Enum;
using DataLayer.Repositories.Abstraction.Schedule;
using Microsoft.EntityFrameworkCore;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace BusinessLayer.Service.ScheduleService
{
    public class AssignService : IAssignService
    {
        private readonly TpeduContext _context;
        private readonly IScheduleUnitOfWork _uow;
        private readonly IStudentProfileService _studentProfileService;
        private readonly IScheduleGenerationService _scheduleGenerationService;

        public AssignService(
            TpeduContext context,
            IScheduleUnitOfWork uow,
            IStudentProfileService studentProfileService,
            IScheduleGenerationService scheduleGenerationService)
        {
            _context = context;
            _uow = uow;
            _studentProfileService = studentProfileService;
            _scheduleGenerationService = scheduleGenerationService;
        }

        public async Task<ClassDto> AssignRecurringClassAsync(string studentUserId, string classId)
        {
            var studentProfileId = await _studentProfileService.GetStudentProfileIdByUserIdAsync(studentUserId);
            if (studentProfileId == null)
                throw new UnauthorizedAccessException("Tài khoản học sinh không hợp lệ.");

            Class targetClass = null;
            var executionStrategy = _context.Database.CreateExecutionStrategy();

            await executionStrategy.ExecuteAsync(async () =>
            {
                await using var transaction = await _context.Database.BeginTransactionAsync();
                try
                {
                    // load Class with Schedules
                    targetClass = await _uow.Classes.GetAsync(
                        filter: c => c.Id == classId,
                        includes: q => q.Include(c => c.ClassSchedules) // <-- Load các Rules
                    );

                    // Validation
                    if (targetClass == null)
                        throw new KeyNotFoundException($"Không tìm thấy lớp học với ID '{classId}'.");

                    // only Pending or Active classes can be assigned
                    if (targetClass.Status != ClassStatus.Pending && targetClass.Status != ClassStatus.Active)
                        throw new InvalidOperationException($"Không thể ghi danh. Lớp học này đang ở trạng thái '{targetClass.Status}'.");

                    if (targetClass.CurrentStudentCount >= targetClass.StudentLimit)
                        throw new InvalidOperationException("Lớp học đã đủ số lượng học sinh.");

                    // check if already assigned
                    var existingAssignment = await _uow.ClassAssigns.GetAsync(
                        a => a.StudentId == studentProfileId && a.ClassId == classId);

                    if (existingAssignment != null)
                        throw new InvalidOperationException("Bạn đã ghi danh vào lớp học này rồi.");

                    // wallet deduction
                    // ex transaction done
                    //  

                    // create ClassAssign
                    var newAssignment = new ClassAssign
                    {
                        Id = Guid.NewGuid().ToString(),
                        ClassId = classId,
                        StudentId = studentProfileId,
                        PaymentStatus = PaymentStatus.Paid, // paid done
                        ApprovalStatus = ApprovalStatus.Approved, // assumed approved
                        EnrolledAt = DateTime.UtcNow
                    };
                    await _uow.ClassAssigns.CreateAsync(newAssignment); // non save

                    // update Class
                    targetClass.CurrentStudentCount++;
                    // if it's the first student, set to Ongoing
                    if (targetClass.Status == ClassStatus.Pending)
                    {
                        targetClass.Status = ClassStatus.Ongoing;
                    }
                    // if reached limit, set to Ongoing
                    else if (targetClass.CurrentStudentCount == targetClass.StudentLimit)
                    {
                        targetClass.Status = ClassStatus.Ongoing;
                    }
                    else
                    {
                        // not full yet, set to Active
                        targetClass.Status = ClassStatus.Active;
                    }

                    await _uow.Classes.UpdateAsync(targetClass); // non save

                    // create Schedule/Lesson if first student
                    if (targetClass.CurrentStudentCount == 1)
                    {
                        await _scheduleGenerationService.GenerateScheduleFromClassAsync(
                            targetClass.Id,
                            targetClass.TutorId,
                            targetClass.ClassStartDate ?? DateTime.UtcNow,
                            targetClass.ClassSchedules // include rules
                        );
                    }

                    // save all
                    await _uow.SaveChangesAsync();
                    await transaction.CommitAsync();
                }
                catch (Exception)
                {
                    await transaction.RollbackAsync();
                    throw;
                }
            });
            return MapToClassDto(targetClass!);
        }

        /// <summary>
        /// [TRANSACTION] student withdraw from class
        /// </summary>
        public async Task<bool> WithdrawFromClassAsync(string studentUserId, string classId)
        {
            var studentProfileId = await _studentProfileService.GetStudentProfileIdByUserIdAsync(studentUserId);
            if (studentProfileId == null)
                throw new UnauthorizedAccessException("Tài khoản học sinh không hợp lệ.");

            var executionStrategy = _context.Database.CreateExecutionStrategy();

            await executionStrategy.ExecuteAsync(async () =>
            {
                await using var transaction = await _context.Database.BeginTransactionAsync();
                try
                {
                    // load ClassAssign
                    var assignment = await _uow.ClassAssigns.GetAsync(
                        a => a.StudentId == studentProfileId && a.ClassId == classId);

                    if (assignment == null)
                        throw new KeyNotFoundException("Bạn chưa ghi danh vào lớp học này.");

                    // load Class
                    var targetClass = await _uow.Classes.GetByIdAsync(classId);
                    if (targetClass == null)
                        throw new KeyNotFoundException("Không tìm thấy lớp học.");

                    // validate Class status
                    if (targetClass.Status == ClassStatus.Pending ||
                        targetClass.Status == ClassStatus.Completed ||
                        targetClass.Status == ClassStatus.Cancelled)
                    {
                        throw new InvalidOperationException($"Không thể rút khỏi lớp học đang ở trạng thái '{targetClass.Status}'.");
                    }

                    // wallet refund
                    // assume done

                    // delete ClassAssign
                    _context.ClassAssigns.Remove(assignment); //using _context to avoid tracking issues

                    // update Class
                    targetClass.CurrentStudentCount--;

                    // if no students left, set to Pending and remove future Lessons/ScheduleEntries
                    if (targetClass.CurrentStudentCount == 0)
                    {
                        targetClass.Status = ClassStatus.Pending;

                        // find future Lessons
                        var futureLessons = await _context.Lessons
                            .Where(l => l.ClassId == classId && l.Status == LessonStatus.SCHEDULED)
                            .ToListAsync();

                        if (futureLessons.Any())
                        {
                            var futureLessonIds = futureLessons.Select(l => l.Id).ToList();

                            var futureEntries = await _context.ScheduleEntries
                                .Where(se => futureLessonIds.Contains(se.LessonId))
                                .ToListAsync();

                            // delete ScheduleEntries
                            _context.ScheduleEntries.RemoveRange(futureEntries);
                            // delete Lessons
                            _context.Lessons.RemoveRange(futureLessons);
                        }
                    }

                    await _uow.Classes.UpdateAsync(targetClass); // non save

                    // save all
                    await _uow.SaveChangesAsync();
                    await transaction.CommitAsync();
                }
                catch (Exception)
                {
                    await transaction.RollbackAsync();
                    throw;
                }
            });

            return true;
        }

        // --- Helper ---
        private ClassDto MapToClassDto(Class cls)
        {
            return new ClassDto
            {
                Id = cls.Id,
                TutorId = cls.TutorId,
                Title = cls.Title,
                Description = cls.Description,
                Subject = cls.Subject,
                EducationLevel = cls.EducationLevel,
                Price = cls.Price ?? 0,
                Status = cls.Status ?? ClassStatus.Pending,
                CreatedAt = cls.CreatedAt,
                UpdatedAt = cls.UpdatedAt,
                Location = cls.Location,
                CurrentStudentCount = cls.CurrentStudentCount,
                StudentLimit = cls.StudentLimit,
                Mode = cls.Mode.ToString(),
                ClassStartDate = cls.ClassStartDate,
                OnlineStudyLink = cls.OnlineStudyLink,
                // non mapped ScheduleRules
            };
        }
    }
}