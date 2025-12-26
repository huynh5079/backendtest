using BusinessLayer.DTOs.Schedule.Class;
using BusinessLayer.DTOs.Wallet;
using BusinessLayer.Helper;
using BusinessLayer.Options;
using BusinessLayer.Service.Interface;
using BusinessLayer.Service.Interface.IScheduleService;
using DataLayer.Entities;
using DataLayer.Enum;
using DataLayer.Repositories.Abstraction;
using DataLayer.Repositories.Abstraction.Schedule;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace BusinessLayer.Service.ScheduleService
{
    public class ClassService : IClassService
    {
        private readonly IScheduleUnitOfWork _uow;
        private readonly IUnitOfWork _mainUow;
        private readonly TpeduContext _context;
        private readonly ITutorProfileService _tutorProfileService;
        private readonly IEscrowService _escrowService;
        private readonly INotificationService _notificationService;
        private readonly IConversationService _conversationService;
        private readonly IScheduleGenerationService _scheduleGenerationService;
        private readonly SystemWalletOptions _systemWalletOptions;

        public ClassService(
            IScheduleUnitOfWork uow,
            IUnitOfWork mainUow,
            TpeduContext context,
            ITutorProfileService tutorProfileService,
            IEscrowService escrowService,
            INotificationService notificationService,
            IConversationService conversationService,
            IScheduleGenerationService scheduleGenerationService,
            IOptions<SystemWalletOptions> systemWalletOptions)
        {
            _uow = uow;
            _mainUow = mainUow;
            _context = context;
            _tutorProfileService = tutorProfileService;
            _escrowService = escrowService;
            _notificationService = notificationService;
            _conversationService = conversationService;
            _scheduleGenerationService = scheduleGenerationService;
            _systemWalletOptions = systemWalletOptions.Value;
        }

        #region Public (Student/Guest)

        public async Task<ClassDto?> GetClassByIdAsync(string classId)
        {
            var targetClass = await _uow.Classes.GetAsync(
                filter: c => c.Id == classId,
                includes: q => q.Include(c => c.ClassSchedules)
                                .Include(c => c.Tutor).ThenInclude(t => t.User)
            );

            if (targetClass == null)
                throw new KeyNotFoundException($"Không tìm thấy lớp học với ID '{classId}'.");

            // include ClassSchedules to MapToClassDto
            return MapToClassDto(targetClass);
        }

        public async Task<IEnumerable<ClassDto>> GetAvailableClassesAsync()
        {
            var now = DateTimeHelper.VietnamNow;

            var classes = await _uow.Classes.GetAllAsync(
                filter: c => (c.Status == ClassStatus.Pending)
                             && c.DeletedAt == null
                             && c.CurrentStudentCount < c.StudentLimit
                             && (c.ClassStartDate == null || c.ClassStartDate > now),
                includes: q => q.Include(c => c.ClassSchedules)
                                .Include(c => c.Tutor).ThenInclude(t => t.User)
            );
            // use MapToClassDto to delegate mapping
            return classes
                .OrderByDescending(c => c.CreatedAt)
                .Select(cls => MapToClassDto(cls));
        }

        public async Task<PaginationResult<ClassDto>> SearchAndFilterAvailableAsync(ClassSearchFilterDto filter)
        {
            var rs = await _uow.Classes.SearchAndFilterAvailableAsync(
                filter.Keyword,
                filter.Subject,
                filter.EducationLevel,
                filter.Mode,
                filter.Area,
                filter.MinPrice,
                filter.MaxPrice,
                filter.Status,
                filter.Page,
                filter.PageSize);

            var mapped = rs.Data.Select(cls => MapToClassDto(cls)).ToList();
            return new PaginationResult<ClassDto>(mapped, rs.TotalCount, rs.PageNumber, rs.PageSize);
        }

        public async Task<IEnumerable<ClassDto>> GetPublicClassesByTutorAsync(string tutorId)
        {
            var classes = await _context.Classes
                .Include(c => c.Tutor)
                    .ThenInclude(t => t.User)
                .Include(c => c.ClassSchedules)
                .Where(c => c.TutorId == tutorId && c.Status == ClassStatus.Pending)
                .ToListAsync();

            return classes.Select(c => MapToClassDto(c));
        }

        #endregion

        #region Tutor Actions

        public async Task<ClassDto> CreateRecurringClassScheduleAsync(string tutorId, CreateClassDto createDto)
        {
            // Validation
            foreach (var rule in createDto.ScheduleRules)
            {
                if (rule.EndTime <= rule.StartTime)
                {
                    throw new InvalidOperationException($"Lịch học {rule.DayOfWeek} có giờ kết thúc sớm hơn giờ bắt đầu.");
                }
            }

            // Kiểm tra duplicate class - Phòng chống trùng lặp
            await CheckForDuplicateClassAsync(tutorId, createDto);

            // Lấy tutorUserId từ tutorId (tutorProfileId)
            var tutorProfile = await _mainUow.TutorProfiles.GetByIdAsync(tutorId);
            if (tutorProfile == null || string.IsNullOrWhiteSpace(tutorProfile.UserId))
            {
                throw new UnauthorizedAccessException("Không tìm thấy thông tin gia sư.");
            }
            string tutorUserId = tutorProfile.UserId;

            // Phí tạo lớp: 50,000 VND
            const decimal CLASS_CREATION_FEE = 50000m;

            // Đảm bảo wallets đã tồn tại trước khi vào transaction
            var tutorWallet = await _mainUow.Wallets.GetByUserIdAsync(tutorUserId);
            if (tutorWallet == null)
            {
                tutorWallet = new Wallet
                {
                    UserId = tutorUserId,
                    Balance = 0,
                    Currency = "VND",
                    IsFrozen = false
                };
                await _mainUow.Wallets.AddAsync(tutorWallet);
                await _mainUow.SaveChangesAsync();
            }

            var adminWallet = await _mainUow.Wallets.GetByUserIdAsync(_systemWalletOptions.SystemWalletUserId);
            if (adminWallet == null)
            {
                adminWallet = new Wallet
                {
                    UserId = _systemWalletOptions.SystemWalletUserId,
                    Balance = 0,
                    Currency = "VND",
                    IsFrozen = false
                };
                await _mainUow.Wallets.AddAsync(adminWallet);
                await _mainUow.SaveChangesAsync();
            }

            // Kiểm tra số dư ví tutor để trả phí tạo lớp
            if (tutorWallet.IsFrozen)
            {
                throw new InvalidOperationException("Ví bị khóa, không thể tạo lớp.");
            }
            if (tutorWallet.Balance < CLASS_CREATION_FEE)
            {
                throw new InvalidOperationException($"Số dư không đủ để tạo lớp. Cần {CLASS_CREATION_FEE:N0} VND, hiện có {tutorWallet.Balance:N0} VND.");
            }

            var newClass = new Class(); // create outside transaction for return purpose
            
            // Sử dụng _mainUow transaction vì cần dùng cả schedule context và main context
            using var transaction = await _mainUow.BeginTransactionAsync();
            try
            {
                newClass = new Class
                {
                    Id = Guid.NewGuid().ToString(),
                    TutorId = tutorId,
                    Title = createDto.Title,
                    Description = createDto.Description,
                    Price = createDto.Price,
                    Subject = createDto.Subject,
                    EducationLevel = createDto.EducationLevel,
                    Location = createDto.Location,
                    Mode = createDto.Mode, // Enum
                    StudentLimit = createDto.StudentLimit,
                    ClassStartDate = createDto.ClassStartDate,
                    OnlineStudyLink = createDto.OnlineStudyLink,
                    Status = ClassStatus.Pending, // wait student enroll
                    CurrentStudentCount = 0
                };

                await _uow.Classes.CreateAsync(newClass);

                // create ClassSchedules from DTO
                var newSchedules = createDto.ScheduleRules.Select(ruleDto => new ClassSchedule
                {
                    Id = Guid.NewGuid().ToString(),
                    ClassId = newClass.Id,
                    DayOfWeek = (byte)ruleDto.DayOfWeek, // Enum -> byte
                    StartTime = ruleDto.StartTime,       // TimeSpan
                    EndTime = ruleDto.EndTime          // TimeSpan
                }).ToList();

                // use _context to add range
                await _context.ClassSchedules.AddRangeAsync(newSchedules);

                // Sinh lịch ngay khi gia sư tạo lớp (dựa trên ClassStartDate hoặc ngày hiện tại)
                try
                {
                    var startDate = newClass.ClassStartDate ?? DateTimeHelper.VietnamNow;
                    Console.WriteLine($"[ClassService.CreateClassAsync] Sinh lịch cho lớp {newClass.Id} khi tạo lớp:");
                    Console.WriteLine($"  - TutorId: {newClass.TutorId}");
                    Console.WriteLine($"  - StartDate: {startDate:dd/MM/yyyy HH:mm:ss}");
                    Console.WriteLine($"  - Số ClassSchedules: {newSchedules.Count}");
                    
                    await _scheduleGenerationService.GenerateScheduleFromClassAsync(
                        newClass.Id,
                        newClass.TutorId,
                        startDate,
                        newSchedules
                    );
                    
                    // Kiểm tra số lượng lessons và schedule entries đã được thêm vào context (chưa SaveChanges)
                    var lessonsInContext = _context.ChangeTracker.Entries<Lesson>()
                        .Where(e => e.Entity.ClassId == newClass.Id && e.State == EntityState.Added)
                        .Count();
                    var scheduleEntriesInContext = _context.ChangeTracker.Entries<ScheduleEntry>()
                        .Where(e => e.Entity.LessonId != null && 
                                   _context.ChangeTracker.Entries<Lesson>()
                                       .Any(le => le.Entity.Id == e.Entity.LessonId && 
                                                  le.Entity.ClassId == newClass.Id && 
                                                  le.State == EntityState.Added) &&
                                   e.State == EntityState.Added)
                        .Count();
                    
                    Console.WriteLine($"[ClassService.CreateClassAsync] Đã thêm vào context (chưa SaveChanges):");
                    Console.WriteLine($"  - Số lessons: {lessonsInContext}");
                    Console.WriteLine($"  - Số schedule entries: {scheduleEntriesInContext}");
                }
                catch (Exception ex)
                {
                    // Log lỗi chi tiết nhưng không throw để không rollback transaction
                    // Lịch sẽ được sinh sau khi có slot trống hoặc khi học sinh thanh toán
                    Console.WriteLine($"[ClassService.CreateClassAsync] LỖI khi sinh lịch cho lớp {newClass.Id} khi tạo lớp:");
                    Console.WriteLine($"  - Message: {ex.Message}");
                    Console.WriteLine($"  - InnerException: {ex.InnerException?.Message}");
                    Console.WriteLine($"  - Stack trace: {ex.StackTrace}");
                    // Không throw exception để không rollback transaction tạo lớp
                }

                // Thu phí tạo lớp: Trừ từ ví tutor, cộng vào ví admin
                tutorWallet.Balance -= CLASS_CREATION_FEE;
                adminWallet.Balance += CLASS_CREATION_FEE;

                await _mainUow.Wallets.Update(tutorWallet);
                await _mainUow.Wallets.Update(adminWallet);

                // Ghi transaction cho tutor (trừ phí)
                await _mainUow.Transactions.AddAsync(new Transaction
                {
                    WalletId = tutorWallet.Id,
                    Type = TransactionType.PayoutOut, // Phí tạo lớp
                    Status = TransactionStatus.Succeeded,
                    Amount = -CLASS_CREATION_FEE,
                    Note = $"Phí tạo lớp {newClass.Id}",
                    CounterpartyUserId = _systemWalletOptions.SystemWalletUserId
                });

                // Ghi transaction cho admin (nhận phí - doanh thu hệ thống)
                await _mainUow.Transactions.AddAsync(new Transaction
                {
                    WalletId = adminWallet.Id,
                    Type = TransactionType.PayoutIn, // Doanh thu từ phí tạo lớp
                    Status = TransactionStatus.Succeeded,
                    Amount = CLASS_CREATION_FEE,
                    Note = $"Phí tạo lớp từ gia sư {tutorUserId} - Lớp {newClass.Id}",
                    CounterpartyUserId = tutorUserId
                });

                // save all
                await _uow.SaveChangesAsync();
                await _mainUow.SaveChangesAsync();
                
                // Kiểm tra lại sau khi SaveChanges để xác nhận đã lưu vào database
                var lessonsCount = await _context.Lessons.CountAsync(l => l.ClassId == newClass.Id && l.DeletedAt == null);
                var scheduleEntriesCount = await _context.ScheduleEntries.CountAsync(se => 
                    se.Lesson != null && 
                    se.Lesson.ClassId == newClass.Id && 
                    se.DeletedAt == null);
                
                if (lessonsCount == 0 || scheduleEntriesCount == 0)
                {
                    Console.WriteLine($"[ClassService.CreateClassAsync] CẢNH BÁO: Sau khi SaveChanges, không tìm thấy lịch cho lớp {newClass.Id}!");
                    Console.WriteLine($"  - Số lessons trong DB: {lessonsCount}");
                    Console.WriteLine($"  - Số schedule entries trong DB: {scheduleEntriesCount}");
                    Console.WriteLine($"  - Có thể do gia sư chưa có lịch rảnh hoặc lịch rảnh bị conflict với ClassSchedules.");
                }
                else
                {
                    Console.WriteLine($"[ClassService.CreateClassAsync] ✅ Đã lưu lịch vào database cho lớp {newClass.Id}:");
                    Console.WriteLine($"  - Số lessons trong DB: {lessonsCount}");
                    Console.WriteLine($"  - Số schedule entries trong DB: {scheduleEntriesCount}");
                }
                
                await transaction.CommitAsync();
            }
            catch
            {
                await transaction.RollbackAsync();
                throw;
            }

            // newClass.ClassSchedules might be null here,
            // so we pass createDto.ScheduleRules to MapToClassDto
            return MapToClassDto(newClass, createDto.ScheduleRules);
        }

        public async Task<IEnumerable<ClassDto>> GetMyClassesAsync(string tutorUserId, ClassStatus? statusFilter = null)
        {
            var tutorProfileId = await _tutorProfileService.GetTutorProfileIdByUserIdAsync(tutorUserId);
            if (tutorProfileId == null)
                throw new UnauthorizedAccessException("Tài khoản gia sư không hợp lệ.");

            var classes = await _uow.Classes.GetAllAsync(
                filter: c => c.TutorId == tutorProfileId 
                                && c.DeletedAt == null
                                && (statusFilter == null || c.Status == statusFilter),
                includes: q => q.Include(c => c.ClassSchedules)
                                .Include(c => c.Tutor).ThenInclude(t => t.User)
            );
            return classes
                .OrderByDescending(c => c.CreatedAt)
                .Select(cls => MapToClassDto(cls));
        }

        public async Task<ClassDto?> UpdateClassAsync(string tutorUserId, string classId, UpdateClassDto dto)
        {
            var tutorProfileId = await _tutorProfileService.GetTutorProfileIdByUserIdAsync(tutorUserId);
            if (tutorProfileId == null)
                throw new UnauthorizedAccessException("Tài khoản gia sư không hợp lệ.");

            var targetClass = await _uow.Classes.GetAsync(
                filter: c => c.Id == classId && c.TutorId == tutorProfileId,
                includes: q => q.Include(c => c.ClassSchedules) // IncludeSchedules to MapToClassDto
            );

            if (targetClass == null)
                throw new KeyNotFoundException("Không tìm thấy lớp học hoặc bạn không có quyền sửa.");

            // only allow update when class is still Pending (no student enrolled)
            if (targetClass.Status != ClassStatus.Pending &&
                targetClass.Status != ClassStatus.Completed &&
                targetClass.Status != ClassStatus.Cancelled)
                throw new InvalidOperationException($"Không thể sửa lớp học đang ở trạng thái '{targetClass.Status}'.");

            // update fields
            targetClass.Title = dto.Title;
            targetClass.Description = dto.Description;
            targetClass.Price = dto.Price;
            targetClass.Location = dto.Location;
            targetClass.StudentLimit = dto.StudentLimit;
            targetClass.OnlineStudyLink = dto.OnlineStudyLink;
            if (dto.Mode.HasValue)
            {
                targetClass.Mode = dto.Mode.Value;
            }

            await _uow.Classes.UpdateAsync(targetClass);
            await _uow.SaveChangesAsync();

            return MapToClassDto(targetClass);
        }

        public async Task<bool> UpdateClassScheduleAsync(string tutorUserId, string classId, UpdateClassScheduleDto dto)
        {
            var tutorProfileId = await _tutorProfileService.GetTutorProfileIdByUserIdAsync(tutorUserId);
            if (tutorProfileId == null)
                throw new UnauthorizedAccessException("Tài khoản gia sư không hợp lệ.");

            // Transaction: delete old schedules, add new schedules
            var executionStrategy = _context.Database.CreateExecutionStrategy();
            await executionStrategy.ExecuteAsync(async () =>
            {
                await using var transaction = await _context.Database.BeginTransactionAsync();
                try
                {
                    var targetClass = await _uow.Classes.GetAsync(
                        filter: c => c.Id == classId && c.TutorId == tutorProfileId);

                    if (targetClass == null)
                        throw new KeyNotFoundException("Không tìm thấy lớp học hoặc bạn không có quyền sửa.");

                    // Important: only allow update when class is still Pending (no student enrolled)
                    if (targetClass.Status != ClassStatus.Pending &&
                        targetClass.Status != ClassStatus.Completed &&
                        targetClass.Status != ClassStatus.Cancelled)
                        throw new InvalidOperationException($"Không thể sửa lịch của lớp học đang ở trạng thái '{targetClass.Status}'.");

                    // delete old schedules
                    var oldSchedules = await _context.ClassSchedules
                        .Where(crs => crs.ClassId == classId)
                        .ToListAsync();
                    _context.ClassSchedules.RemoveRange(oldSchedules);

                    // add new schedules from dto
                    var newSchedules = dto.ScheduleRules.Select(s => new ClassSchedule
                    {
                        Id = Guid.NewGuid().ToString(),
                        ClassId = classId,
                        DayOfWeek = (byte)s.DayOfWeek,
                        StartTime = s.StartTime,
                        EndTime = s.EndTime
                    }).ToList();
                    await _context.ClassSchedules.AddRangeAsync(newSchedules);

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

        public async Task<bool> DeleteClassAsync(string tutorUserId, string classId)
        {
            var tutorProfileId = await _tutorProfileService.GetTutorProfileIdByUserIdAsync(tutorUserId);
            if (tutorProfileId == null)
                throw new UnauthorizedAccessException("Tài khoản gia sư không hợp lệ.");

            // Transaction delete ClassSchedules and Class
            var executionStrategy = _context.Database.CreateExecutionStrategy();
            await executionStrategy.ExecuteAsync(async () =>
            {
                await using var transaction = await _context.Database.BeginTransactionAsync();
                try
                {
                    var targetClass = await _uow.Classes.GetAsync(
                        filter: c => c.Id == classId && c.TutorId == tutorProfileId,
                        includes: q => q.Include(c => c.ClassSchedules)
                                        .Include(c => c.Lessons)
                    );

                    if (targetClass == null)
                        throw new KeyNotFoundException("Không tìm thấy lớp học hoặc bạn không có quyền xóa.");

                    if (targetClass.Status != ClassStatus.Pending &&
                        targetClass.Status != ClassStatus.Completed &&
                        targetClass.Status != ClassStatus.Cancelled)
                        throw new InvalidOperationException($"Không thể xóa lớp học đang ở trạng thái '{targetClass.Status}'.");

                    // Nếu lớp đã hủy (Cancelled), cho phép xóa ngay cả khi có ClassAssign
                    // Vì lớp đã hủy rồi, không còn hoạt động
                    if (targetClass.Status != ClassStatus.Cancelled)
                    {
                        // Kiểm tra xem có học sinh đã đăng ký chưa (chỉ áp dụng cho Pending và Completed)
                        var hasEnrollments = await _context.ClassAssigns.AnyAsync(ca => ca.ClassId == classId);
                        if (hasEnrollments)
                            throw new InvalidOperationException("Không thể xóa lớp học đã có học sinh đăng ký. Vui lòng hủy lớp học thay vì xóa.");
                    }

                    var now = DateTimeHelper.VietnamNow;

                    // Soft delete Class
                    targetClass.DeletedAt = now;
                    targetClass.UpdatedAt = now;

                    // Hard delete ClassSchedules
                    if (targetClass.ClassSchedules != null && targetClass.ClassSchedules.Any())
                    {
                        _context.ClassSchedules.RemoveRange(targetClass.ClassSchedules);
                    }

                    // Hard delete Lessons và ScheduleEntries để tránh trùng lịch
                    var allLessons = targetClass.Lessons?.Where(l => l.DeletedAt == null).ToList() 
                                     ?? await _context.Lessons
                                         .Where(l => l.ClassId == classId && l.DeletedAt == null)
                                         .ToListAsync();

                    if (allLessons.Any())
                    {
                        var lessonIds = allLessons.Select(l => l.Id).ToList();
                        var allScheduleEntries = await _context.ScheduleEntries
                            .Where(se => lessonIds.Contains(se.LessonId) && se.DeletedAt == null)
                            .ToListAsync();

                        // Hard delete tất cả schedule entries trước (vì có foreign key)
                        _context.ScheduleEntries.RemoveRange(allScheduleEntries);
                        
                        // Hard delete tất cả lessons
                        _context.Lessons.RemoveRange(allLessons);
                        
                        Console.WriteLine($"[DeleteClassAsync] Đã xóa vĩnh viễn {allLessons.Count} lessons và {allScheduleEntries.Count} schedule entries của lớp {classId}");
                    }

                    // 3. Save
                    await _uow.Classes.UpdateAsync(targetClass);
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

        public async Task<bool> CompleteClassAsync(string tutorUserId, string classId)
        {
            var tutorProfileId = await _tutorProfileService.GetTutorProfileIdByUserIdAsync(tutorUserId);
            if (tutorProfileId == null)
                throw new UnauthorizedAccessException("Tài khoản gia sư không hợp lệ.");

            var targetClass = await _uow.Classes.GetAsync(
                filter: c => c.Id == classId && c.TutorId == tutorProfileId);

            if (targetClass == null)
                throw new KeyNotFoundException("Không tìm thấy lớp học hoặc bạn không có quyền.");

            // Kiểm tra số buổi đã hoàn thành
            var completedLessons = await _context.Lessons
                .Where(l => l.ClassId == classId && l.Status == LessonStatus.COMPLETED)
                .CountAsync();
            
            var totalLessons = await _context.Lessons
                .Where(l => l.ClassId == classId)
                .CountAsync();

            if (totalLessons == 0)
                throw new InvalidOperationException("Lớp học chưa có buổi học nào. Không thể hoàn thành.");

            // Kiểm tra xem escrow đã được giải ngân chưa
            var allEscrows = await _mainUow.Escrows.GetAllAsync(
                filter: e => e.ClassId == classId);
            
            bool allEscrowsReleased = false;
            if (allEscrows.Any())
            {
                allEscrowsReleased = allEscrows.All(e => 
                    e.Status == EscrowStatus.Released || e.Status == EscrowStatus.PartiallyReleased);
            }

            // Nếu escrow đã được giải ngân rồi → chỉ cần kiểm tra và cập nhật status
            if (allEscrowsReleased)
            {
                // Nếu tất cả buổi học đã hoàn thành (100%) → tự động chuyển class status = Completed
                if (totalLessons > 0 && completedLessons == totalLessons)
                {
                    if (targetClass.Status != ClassStatus.Completed)
                    {
                        targetClass.Status = ClassStatus.Completed;
                        await _uow.Classes.UpdateAsync(targetClass);
                        await _uow.SaveChangesAsync();
                    }
                    return true; // Đã hoàn thành cả escrow và lessons
                }
                else
                {
                    // Escrow đã giải ngân nhưng chưa học hết → lớp vẫn Ongoing
                    throw new InvalidOperationException(
                        "Escrow đã được giải ngân. Lớp học vẫn đang tiếp tục để hoàn thành các buổi học còn lại.");
                }
            }

            if (targetClass.Status != ClassStatus.Ongoing)
                throw new InvalidOperationException($"Chỉ có thể hoàn thành lớp học đang ở trạng thái Ongoing.");

            // Kiểm tra số buổi đã hoàn thành ≥ 90% (để giải ngân escrow)
            decimal completionPercentage = (decimal)completedLessons / totalLessons;
            const decimal REQUIRED_COMPLETION_PERCENTAGE = 0.9m; // 90%

            if (completionPercentage < REQUIRED_COMPLETION_PERCENTAGE)
            {
                throw new InvalidOperationException(
                    $"Không thể hoàn thành lớp học. Yêu cầu hoàn thành ít nhất {REQUIRED_COMPLETION_PERCENTAGE * 100}% số buổi học. " +
                    $"Hiện tại: {completedLessons}/{totalLessons} buổi ({completionPercentage * 100:F1}%).");
            }

            // Kiểm tra duplicate prevention - Đảm bảo escrow chưa được release
            // Tìm tất cả Escrow của lớp này (với lớp group có nhiều escrow)
            var escrows = await _mainUow.Escrows.GetAllAsync(
                filter: e => e.ClassId == classId && e.Status == EscrowStatus.Held);

            if (!escrows.Any())
            {
                throw new InvalidOperationException("Không tìm thấy escrow cho lớp học này. Vui lòng kiểm tra lại.");
            }

            // LƯU Ý: KHÔNG đổi status sang Completed để học sinh vẫn có thể tiếp tục học các buổi còn lại
            // Chỉ giải ngân escrow (hoàn thành khóa học về mặt thanh toán), nhưng lớp vẫn tiếp tục học
            // Status vẫn giữ nguyên Ongoing để không block việc điểm danh và hoàn thành các buổi học còn lại

            // Giải ngân tất cả escrow: Hoàn cọc (1 lần) + Giải ngân học phí (từng escrow) + Commission
            // Lưu ý: Deposit chỉ hoàn 1 lần cho cả lớp, không phải từng escrow
            bool depositRefunded = false;
            var errors = new List<string>();

            foreach (var escrow in escrows)
            {
                var releaseResult = await _escrowService.ReleaseAsync(tutorUserId, new ReleaseEscrowRequest { EscrowId = escrow.Id });
                
                if (releaseResult.Status == "Fail")
                {
                    errors.Add($"Escrow {escrow.Id}: {releaseResult.Message}");
                }
                else
                {
                    // Đánh dấu đã hoàn cọc (chỉ 1 lần cho cả lớp)
                    depositRefunded = true;
                }
            }
            
            if (errors.Any())
            {
                // Không cần rollback status vì không đổi status
                throw new InvalidOperationException($"Không thể giải ngân một số escrow: {string.Join("; ", errors)}");
            }

            // Sau khi giải ngân escrow thành công, kiểm tra xem tất cả buổi học đã hoàn thành chưa
            // Nếu đã hoàn thành 100% → tự động chuyển class status = Completed
            var allCompletedLessons = await _context.Lessons
                .Where(l => l.ClassId == classId && l.Status == LessonStatus.COMPLETED)
                .CountAsync();
            
            var totalLessonsAfterRelease = await _context.Lessons
                .Where(l => l.ClassId == classId)
                .CountAsync();

            // Nếu tất cả buổi học đã hoàn thành (100%) → chuyển class status = Completed
            if (totalLessonsAfterRelease > 0 && allCompletedLessons == totalLessonsAfterRelease)
            {
                targetClass.Status = ClassStatus.Completed;
                await _uow.Classes.UpdateAsync(targetClass);
                await _uow.SaveChangesAsync();

                // Xóa conversation của lớp khi hoàn thành
                try
                {
                    await _conversationService.DeleteClassConversationAsync(classId);
                }
                catch (Exception convEx)
                {
                    Console.WriteLine($"CompleteClassAsync: Lỗi khi xóa conversation: {convEx.Message}");
                }
            }

            return true;
        }

        /// <summary>
        /// Tutor hủy lớp sớm hoặc bỏ giữa chừng
        /// - Tutor cancel sớm (chưa dạy hoặc mới dạy ít buổi) → Refund full cho HS, Forfeit deposit
        /// - Tutor bỏ giữa chừng (đã dạy một phần) → Partial release cho tutor, refund phần còn lại cho HS, Forfeit deposit
        /// </summary>
        public async Task<CancelClassResponseDto> CancelClassByTutorAsync(string tutorUserId, string classId, string? reason)
        {
            var tutorProfileId = await _tutorProfileService.GetTutorProfileIdByUserIdAsync(tutorUserId);
            if (tutorProfileId == null)
                throw new UnauthorizedAccessException("Tài khoản gia sư không hợp lệ.");

            var classEntity = await _uow.Classes.GetByIdAsync(classId);
            if (classEntity == null)
                throw new KeyNotFoundException("Không tìm thấy lớp học.");

            // Kiểm tra quyền sở hữu
            if (classEntity.TutorId != tutorProfileId)
                throw new UnauthorizedAccessException("Bạn không có quyền hủy lớp học này.");

            // Không cho phép hủy nếu đã Completed hoặc Cancelled
            if (classEntity.Status == ClassStatus.Completed)
                throw new InvalidOperationException("Không thể hủy lớp đã hoàn thành.");
            if (classEntity.Status == ClassStatus.Cancelled)
                throw new InvalidOperationException("Lớp học đã bị hủy rồi.");

            // Kiểm tra số buổi đã học
            var completedLessons = await _context.Lessons
                .Where(l => l.ClassId == classId && l.Status == LessonStatus.COMPLETED)
                .CountAsync();
            
            var totalLessons = await _context.Lessons
                .Where(l => l.ClassId == classId)
                .CountAsync();

            bool hasStartedTeaching = completedLessons > 0;
            decimal teachingPercentage = totalLessons > 0 
                ? (decimal)completedLessons / totalLessons 
                : 0;

            // Lấy tất cả escrow của lớp
            var escrows = await _mainUow.Escrows.GetAllAsync(
                filter: e => e.ClassId == classId && 
                           (e.Status == EscrowStatus.Held || e.Status == EscrowStatus.PartiallyReleased));

            // Lấy tutor deposit
            var tutorDeposit = await _mainUow.TutorDepositEscrows.GetByClassIdAsync(classId);

            // Đảm bảo wallets đã tồn tại trước khi vào transaction
            var adminWallet = await _mainUow.Wallets.GetByUserIdAsync(_systemWalletOptions.SystemWalletUserId);
            if (adminWallet == null)
            {
                adminWallet = new Wallet
                {
                    UserId = _systemWalletOptions.SystemWalletUserId,
                    Balance = 0,
                    Currency = "VND",
                    IsFrozen = false
                };
                await _mainUow.Wallets.AddAsync(adminWallet);
                await _mainUow.SaveChangesAsync();
            }

            // Đảm bảo tất cả student wallets và tutor wallet đã tồn tại
            var studentUserIds = escrows.Select(e => e.StudentUserId).Distinct().ToList();
            foreach (var studentUserId in studentUserIds)
            {
                var studentWallet = await _mainUow.Wallets.GetByUserIdAsync(studentUserId);
                if (studentWallet == null)
                {
                    studentWallet = new Wallet
                    {
                        UserId = studentUserId,
                        Balance = 0,
                        Currency = "VND",
                        IsFrozen = false
                    };
                    await _mainUow.Wallets.AddAsync(studentWallet);
                }
            }

            if (tutorDeposit != null && !string.IsNullOrEmpty(tutorDeposit.TutorUserId))
            {
                var tutorWallet = await _mainUow.Wallets.GetByUserIdAsync(tutorDeposit.TutorUserId);
                if (tutorWallet == null)
                {
                    tutorWallet = new Wallet
                    {
                        UserId = tutorDeposit.TutorUserId,
                        Balance = 0,
                        Currency = "VND",
                        IsFrozen = false
                    };
                    await _mainUow.Wallets.AddAsync(tutorWallet);
                }
            }

            await _mainUow.SaveChangesAsync();

            using var tx = await _mainUow.BeginTransactionAsync();

            int refundedCount = 0;
            decimal totalRefunded = 0;
            bool depositForfeited = false;
            decimal? depositForfeitAmount = null;

            try
            {
                if (!hasStartedTeaching)
                {
                    // Tutor cancel sớm - Chưa dạy buổi nào
                    // → Refund full cho tất cả học sinh, Forfeit deposit về ví admin
                    foreach (var esc in escrows)
                    {
                        if (esc.Status == EscrowStatus.Held)
                        {
                            // Refund full - sử dụng helper trong transaction
                            var refundSuccess = await RefundEscrowInTransactionAsync(tutorUserId, esc);
                            if (refundSuccess)
                            {
                                refundedCount++;
                                totalRefunded += esc.GrossAmount;
                            }
                            else
                            {
                                throw new InvalidOperationException($"Không thể hoàn tiền escrow {esc.Id}");
                            }
                        }
                        else if (esc.Status == EscrowStatus.PartiallyReleased)
                        {
                            // Refund phần còn lại
                            decimal remainingPercentage = 1.0m - (esc.ReleasedAmount / esc.GrossAmount);
                            if (remainingPercentage > 0)
                            {
                                var partialRefundSuccess = await PartialRefundEscrowInTransactionAsync(tutorUserId, esc, remainingPercentage);
                                if (partialRefundSuccess)
                                {
                                    refundedCount++;
                                    totalRefunded += esc.GrossAmount * remainingPercentage;
                                }
                                else
                                {
                                    throw new InvalidOperationException($"Không thể hoàn tiền escrow {esc.Id}");
                                }
                            }
                        }
                    }

                    // Forfeit deposit về ví admin (system)
                    if (tutorDeposit != null && tutorDeposit.Status == TutorDepositStatus.Held)
                    {
                        var forfeitSuccess = await ForfeitDepositInTransactionAsync(tutorUserId, tutorDeposit, false, $"Gia sư hủy lớp sớm (chưa dạy buổi nào): {reason ?? "Không có lý do"}");
                        depositForfeited = forfeitSuccess;
                        if (depositForfeited)
                        {
                            depositForfeitAmount = tutorDeposit.DepositAmount;
                        }
                    }
                }
                else
                {
                    // Tutor bỏ giữa chừng - Đã dạy một phần
                    // → Partial release cho tutor theo % đã dạy, refund phần còn lại cho HS, Forfeit deposit
                    foreach (var esc in escrows)
                    {
                        if (esc.Status == EscrowStatus.Held || esc.Status == EscrowStatus.PartiallyReleased)
                        {
                            // Release cho tutor theo % đã dạy
                            decimal alreadyReleasedPercentage = esc.Status == EscrowStatus.PartiallyReleased
                                ? esc.ReleasedAmount / esc.GrossAmount
                                : 0m;

                            decimal remainingToRelease = teachingPercentage - alreadyReleasedPercentage;
                            if (remainingToRelease > 0)
                            {
                                var releaseSuccess = await PartialReleaseEscrowInTransactionAsync(tutorUserId, esc, remainingToRelease);
                                if (!releaseSuccess)
                                {
                                    throw new InvalidOperationException($"Không thể giải ngân escrow {esc.Id}");
                                }
                            }

                            // Refund phần còn lại cho HS
                            decimal remainingPercentage = 1.0m - teachingPercentage;
                            if (remainingPercentage > 0)
                            {
                                var partialRefundSuccess = await PartialRefundEscrowInTransactionAsync(tutorUserId, esc, remainingPercentage);
                                if (partialRefundSuccess)
                                {
                                    refundedCount++;
                                    totalRefunded += esc.GrossAmount * remainingPercentage;
                                }
                                else
                                {
                                    throw new InvalidOperationException($"Không thể hoàn tiền escrow {esc.Id}");
                                }
                            }
                        }
                    }

                    // Forfeit deposit về ví admin
                    if (tutorDeposit != null && tutorDeposit.Status == TutorDepositStatus.Held)
                    {
                        var forfeitSuccess = await ForfeitDepositInTransactionAsync(tutorUserId, tutorDeposit, false, $"Gia sư bỏ giữa chừng (đã dạy {completedLessons}/{totalLessons} buổi): {reason ?? "Không có lý do"}");
                        depositForfeited = forfeitSuccess;
                        if (depositForfeited)
                        {
                            depositForfeitAmount = tutorDeposit.DepositAmount;
                        }
                    }
                }

                // Cập nhật trạng thái lớp
                classEntity.Status = ClassStatus.Cancelled;
                await _uow.Classes.UpdateAsync(classEntity);

                // Xóa TẤT CẢ lessons và schedule entries (hard delete để tránh trùng lịch)
                var allLessons = await _context.Lessons
                    .Where(l => l.ClassId == classId && l.DeletedAt == null)
                    .ToListAsync();

                if (allLessons.Any())
                {
                    var lessonIds = allLessons.Select(l => l.Id).ToList();
                    var allScheduleEntries = await _context.ScheduleEntries
                        .Where(se => lessonIds.Contains(se.LessonId) && se.DeletedAt == null)
                        .ToListAsync();

                    // Hard delete tất cả schedule entries trước (vì có foreign key)
                    _context.ScheduleEntries.RemoveRange(allScheduleEntries);
                    
                    // Hard delete tất cả lessons
                    _context.Lessons.RemoveRange(allLessons);
                    
                    Console.WriteLine($"[CancelClassByTutorAsync] Đã xóa vĩnh viễn {allLessons.Count} lessons và {allScheduleEntries.Count} schedule entries của lớp {classId}");
                }

                await _uow.SaveChangesAsync();
                await _mainUow.SaveChangesAsync();
                await tx.CommitAsync();

                // Gửi notification sau khi commit transaction
                // Reload escrows để có thông tin mới nhất
                var refundedEscrows = await _mainUow.Escrows.GetAllAsync(
                    filter: e => e.ClassId == classId && 
                               (e.Status == EscrowStatus.Refunded || e.Status == EscrowStatus.PartiallyReleased));

                // Gửi notification về việc hoàn tiền cho các học sinh đã được refund
                foreach (var esc in refundedEscrows)
                {
                    try
                    {
                        decimal refundAmount = esc.RefundedAmount;
                        if (refundAmount > 0)
                        {
                            var refundNotification = await _notificationService.CreateEscrowNotificationAsync(
                                esc.StudentUserId,
                                NotificationType.EscrowRefunded,
                                refundAmount,
                                esc.ClassId,
                                esc.Id);
                            await _mainUow.SaveChangesAsync();
                            await _notificationService.SendRealTimeNotificationAsync(esc.StudentUserId, refundNotification);
                        }
                    }
                    catch (Exception notifEx)
                    {
                        Console.WriteLine($"CancelClassByTutorAsync: Lỗi khi gửi notification hoàn tiền cho student {esc.StudentUserId}: {notifEx.Message}");
                    }
                }

                // Gửi notification về việc hủy lớp cho tất cả học sinh
                var allStudentUserIds = escrows.Select(e => e.StudentUserId).Distinct().ToList();
                foreach (var studentUserId in allStudentUserIds)
                {
                    try
                    {
                        string message = hasStartedTeaching
                            ? $"Gia sư đã hủy lớp học (đã dạy {completedLessons}/{totalLessons} buổi). {reason ?? ""}"
                            : $"Gia sư đã hủy lớp học trước khi bắt đầu. {reason ?? ""}";
                        
                        var studentNotification = await _notificationService.CreateAccountNotificationAsync(
                            studentUserId,
                            NotificationType.ClassCancelled,
                            message,
                            classId);
                        await _uow.SaveChangesAsync();
                        await _notificationService.SendRealTimeNotificationAsync(studentUserId, studentNotification);
                    }
                    catch (Exception notifEx)
                    {
                        Console.WriteLine($"CancelClassByTutorAsync: Lỗi khi gửi notification hủy lớp cho student {studentUserId}: {notifEx.Message}");
                    }
                }

                // Xóa conversation của lớp khi hủy
                try
                {
                    await _conversationService.DeleteClassConversationAsync(classId);
                }
                catch (Exception convEx)
                {
                    Console.WriteLine($"CancelClassByTutorAsync: Lỗi khi xóa conversation: {convEx.Message}");
                }

                return new CancelClassResponseDto
                {
                    ClassId = classId,
                    NewStatus = ClassStatus.Cancelled,
                    Reason = ClassCancelReason.TutorFault,
                    RefundedEscrowsCount = refundedCount,
                    TotalRefundedAmount = totalRefunded,
                    DepositRefunded = depositForfeited,
                    DepositRefundAmount = depositForfeitAmount,
                    Message = hasStartedTeaching
                        ? $"Đã hủy lớp. Đã hoàn {refundedCount} escrow, tổng {totalRefunded:N0} VND. Deposit đã bị tịch thu."
                        : $"Đã hủy lớp sớm. Đã hoàn {refundedCount} escrow, tổng {totalRefunded:N0} VND. Deposit đã bị tịch thu."
                };
            }
            catch (Exception)
            {
                await tx.RollbackAsync();
                throw;
            }
        }

        #endregion

        #region Admin Actions

        /// <summary>
        /// Admin lấy tất cả lớp học (bao gồm tất cả status, không filter theo CurrentStudentCount)
        /// </summary>
        public async Task<IEnumerable<ClassDto>> GetAllClassesForAdminAsync(ClassStatus? statusFilter = null)
        {
            var classes = await _uow.Classes.GetAllAsync(
                filter: c => c.DeletedAt == null
                             && (statusFilter == null || c.Status == statusFilter),
                includes: q => q.Include(c => c.ClassSchedules)
                                .Include(c => c.Tutor).ThenInclude(t => t.User)
            );
            return classes
                .OrderByDescending(c => c.CreatedAt)
                .Select(cls => MapToClassDto(cls));
        }

        /// <summary>
        /// Admin hủy lớp 
        /// </summary>
        public async Task<CancelClassResponseDto> CancelClassByAdminAsync(string adminUserId, CancelClassRequestDto request)
        {
            var classEntity = await _uow.Classes.GetByIdAsync(request.ClassId);
            if (classEntity == null)
                throw new KeyNotFoundException("Không tìm thấy lớp học.");

            // Không cho phép hủy nếu đã Completed
            if (classEntity.Status == ClassStatus.Completed)
                throw new InvalidOperationException("Không thể hủy lớp đã hoàn thành.");

            // Kiểm tra số buổi đã học
            var completedLessons = await _context.Lessons
                .Where(l => l.ClassId == request.ClassId && l.Status == LessonStatus.COMPLETED)
                .CountAsync();
            
            var totalLessons = await _context.Lessons
                .Where(l => l.ClassId == request.ClassId)
                .CountAsync();

            bool hasStartedTeaching = completedLessons > 0; // Đã có ít nhất 1 buổi completed

            // Kiểm tra ≥80% hoàn thành - Không cho phép hủy nếu đã hoàn thành ≥80% (trừ một số lý do đặc biệt)
            decimal completionPercentage = totalLessons > 0 
                ? (decimal)completedLessons / totalLessons 
                : 0;
            
            bool isEightyPercentOrMoreCompleted = completionPercentage >= 0.8m;
            
            // Nếu đã hoàn thành ≥80%, chỉ cho phép hủy với các lý do đặc biệt
            if (isEightyPercentOrMoreCompleted)
            {
                bool isAllowedReason = request.Reason == ClassCancelReason.SystemError 
                                    || request.Reason == ClassCancelReason.PolicyViolation
                                    || request.Reason == ClassCancelReason.DuplicateClass;
                
                if (!isAllowedReason)
                {
                    throw new InvalidOperationException(
                        $"Không thể hủy lớp học đã hoàn thành {completionPercentage:P0} ({completedLessons}/{totalLessons} buổi). " +
                        "Chỉ có thể hủy với lý do: Lỗi hệ thống, Vi phạm chính sách, hoặc Lớp trùng lặp.");
                }
            }

            // Lấy tutor deposit
            var tutorDeposit = await _mainUow.TutorDepositEscrows.GetByClassIdAsync(request.ClassId);

            // Lấy tất cả escrow của lớp TRƯỚC transaction để đảm bảo wallets được tạo trước
            var escrows = await _mainUow.Escrows.GetAllAsync(
                filter: e => e.ClassId == request.ClassId && 
                           (e.Status == EscrowStatus.Held || e.Status == EscrowStatus.PartiallyReleased));

            // Đảm bảo wallets đã tồn tại trước khi vào transaction
            var adminWallet = await _mainUow.Wallets.GetByUserIdAsync(_systemWalletOptions.SystemWalletUserId);
            if (adminWallet == null)
            {
                adminWallet = new Wallet
                {
                    UserId = _systemWalletOptions.SystemWalletUserId,
                    Balance = 0,
                    Currency = "VND",
                    IsFrozen = false
                };
                await _mainUow.Wallets.AddAsync(adminWallet);
                await _mainUow.SaveChangesAsync();
            }

            // Đảm bảo tất cả student wallets và tutor wallet đã tồn tại
            var studentUserIds = escrows.Select(e => e.StudentUserId).Distinct().ToList();
            foreach (var studentUserId in studentUserIds)
            {
                var studentWallet = await _mainUow.Wallets.GetByUserIdAsync(studentUserId);
                if (studentWallet == null)
                {
                    studentWallet = new Wallet
                    {
                        UserId = studentUserId,
                        Balance = 0,
                        Currency = "VND",
                        IsFrozen = false
                    };
                    await _mainUow.Wallets.AddAsync(studentWallet);
                }
            }

            if (tutorDeposit != null && !string.IsNullOrEmpty(tutorDeposit.TutorUserId))
            {
                var tutorWallet = await _mainUow.Wallets.GetByUserIdAsync(tutorDeposit.TutorUserId);
                if (tutorWallet == null)
                {
                    tutorWallet = new Wallet
                    {
                        UserId = tutorDeposit.TutorUserId,
                        Balance = 0,
                        Currency = "VND",
                        IsFrozen = false
                    };
                    await _mainUow.Wallets.AddAsync(tutorWallet);
                }
            }

            await _mainUow.SaveChangesAsync();

            using var tx = await _mainUow.BeginTransactionAsync();

            Console.WriteLine($"CancelClassByAdminAsync: Tìm thấy {escrows.Count()} escrow(s) cho lớp {request.ClassId}");
            foreach (var esc in escrows)
            {
                Console.WriteLine($"  - Escrow {esc.Id}: Status={esc.Status}, Amount={esc.GrossAmount:N0}, StudentUserId={esc.StudentUserId}");
            }

            int refundedCount = 0;
            decimal totalRefunded = 0;
            bool depositRefunded = false;
            decimal? depositRefundAmount = null;
            
            // Lưu danh sách escrows đã refund để gửi notification sau
            var refundedEscrows = new List<Escrow>();

            // Xử lý theo reason
            switch (request.Reason)
            {
                case ClassCancelReason.SystemError:
                case ClassCancelReason.PolicyViolation:
                case ClassCancelReason.DuplicateClass:
                case ClassCancelReason.IncorrectInfo:
                    // Lỗi hệ thống → Refund full
                    // Refund tất cả escrow
                    Console.WriteLine($"CancelClassByAdminAsync: Bắt đầu refund {escrows.Count()} escrow(s) cho case SystemError/PolicyViolation/DuplicateClass/IncorrectInfo");
                    foreach (var esc in escrows)
                    {
                        Console.WriteLine($"CancelClassByAdminAsync: [{refundedCount + 1}/{escrows.Count()}] Đang refund escrow {esc.Id}, Status={esc.Status}, Amount={esc.GrossAmount:N0}");
                        
                        // Lưu thông tin escrow trước khi refund
                        var escrowId = esc.Id;
                        var escrowStatus = esc.Status;
                        var grossAmount = esc.GrossAmount;
                        // Tính số tiền sẽ refund: Held = full, PartiallyReleased = phần còn lại
                        decimal expectedRefundAmount = escrowStatus == EscrowStatus.Held 
                            ? grossAmount 
                            : (grossAmount - esc.ReleasedAmount - esc.RefundedAmount);
                        
                        Console.WriteLine($"CancelClassByAdminAsync: Escrow {escrowId} - Expected refund amount: {expectedRefundAmount:N0}");
                        var refundSuccess = await RefundEscrowInTransactionAsync(adminUserId, esc);
                        if (refundSuccess)
                        {
                            refundedCount++;
                            totalRefunded += expectedRefundAmount;
                            
                            // Reload escrow để lưu vào list
                            var escForNotification = await _mainUow.Escrows.GetByIdAsync(escrowId);
                            if (escForNotification != null)
                            {
                                refundedEscrows.Add(escForNotification);
                            }
                            
                            Console.WriteLine($"CancelClassByAdminAsync: ✅ Refund thành công escrow {escrowId}, số tiền: {expectedRefundAmount:N0} (Đã refund {refundedCount}/{escrows.Count()})");
                        }
                        else
                        {
                            Console.WriteLine($"CancelClassByAdminAsync: ❌ Lỗi refund escrow {escrowId}");
                            throw new InvalidOperationException($"Không thể hoàn tiền escrow {escrowId}");
                        }
                    }

                    // Refund deposit nếu có (hoàn 100% cho tutor vì lỗi hệ thống)
                    if (tutorDeposit != null && tutorDeposit.Status == TutorDepositStatus.Held)
                    {
                        var refundDepositResult = await RefundTutorDepositAsync(adminUserId, tutorDeposit, "Admin hủy lớp do lỗi hệ thống/setup");
                        if (refundDepositResult)
                        {
                            depositRefunded = true;
                            depositRefundAmount = tutorDeposit.DepositAmount;
                        }
                    }
                    break;

                case ClassCancelReason.TutorFault:
                    // Tutor lỗi
                    Console.WriteLine($"CancelClassByAdminAsync: TutorFault, hasStartedTeaching={hasStartedTeaching}, escrows count={escrows.Count()}");
                    if (!hasStartedTeaching)
                    {
                        // Chưa dạy → Refund full HS, Forfeit deposit về ví admin
                        Console.WriteLine($"CancelClassByAdminAsync: TutorFault - chưa dạy, refund full cho {escrows.Count()} escrow(s)");
                        foreach (var esc in escrows)
                        {
                            Console.WriteLine($"CancelClassByAdminAsync: Đang refund escrow {esc.Id}, Status={esc.Status}, Amount={esc.GrossAmount:N0}, StudentUserId={esc.StudentUserId}");
                            
                            // Lưu thông tin escrow trước khi refund
                            var escrowId = esc.Id;
                            var escrowStatus = esc.Status;
                            var grossAmount = esc.GrossAmount;
                            // Tính số tiền sẽ refund: Held = full, PartiallyReleased = phần còn lại
                            decimal expectedRefundAmount = escrowStatus == EscrowStatus.Held 
                                ? grossAmount 
                                : (grossAmount - esc.ReleasedAmount - esc.RefundedAmount);
                            
                            var refundSuccess = await RefundEscrowInTransactionAsync(adminUserId, esc);
                            if (refundSuccess)
                            {
                                refundedCount++;
                                totalRefunded += expectedRefundAmount;
                                
                                // Reload escrow để lưu vào list
                                var escForNotification = await _mainUow.Escrows.GetByIdAsync(escrowId);
                                if (escForNotification != null)
                                {
                                    refundedEscrows.Add(escForNotification);
                                }
                                
                                Console.WriteLine($"CancelClassByAdminAsync: Refund thành công escrow {escrowId}, số tiền: {expectedRefundAmount:N0}, tổng refunded: {totalRefunded:N0}");
                            }
                            else
                            {
                                Console.WriteLine($"CancelClassByAdminAsync: Lỗi refund escrow {escrowId} (TutorFault - chưa dạy)");
                                throw new InvalidOperationException($"Không thể hoàn tiền escrow {escrowId}");
                            }
                        }

                        // Forfeit deposit - trả về ví admin (system) như đền bù
                        if (tutorDeposit != null && tutorDeposit.Status == TutorDepositStatus.Held)
                        {
                            var forfeitSuccess = await ForfeitDepositInTransactionAsync(adminUserId, tutorDeposit, false, $"Admin hủy lớp do lỗi tutor: {request.Note}");
                            depositRefunded = forfeitSuccess;
                            if (depositRefunded)
                            {
                                depositRefundAmount = tutorDeposit.DepositAmount;
                            }
                        }
                    }
                    else
                    {
                        // Đã dạy một phần → Partial release cho tutor, refund phần còn lại cho HS
                        // Tính % đã dạy (dùng biến đã khai báo ở scope ngoài)
                        decimal tutorFaultTeachingPercentage = totalLessons > 0 
                            ? (decimal)completedLessons / totalLessons 
                            : 0;
                        
                        // Release cho tutor theo % đã dạy
                        foreach (var esc in escrows)
                        {
                            if (esc.Status == EscrowStatus.Held || esc.Status == EscrowStatus.PartiallyReleased)
                            {
                                var releaseSuccess = await PartialReleaseEscrowInTransactionAsync(adminUserId, esc, tutorFaultTeachingPercentage);
                                if (!releaseSuccess)
                                {
                                    throw new InvalidOperationException($"Không thể giải ngân escrow {esc.Id}");
                                }

                                // Refund phần còn lại cho HS (100% - % đã dạy)
                                decimal refundPercentage = 1.0m - tutorFaultTeachingPercentage;
                                if (refundPercentage > 0)
                                {
                                    var partialRefundSuccess = await PartialRefundEscrowInTransactionAsync(adminUserId, esc, refundPercentage);
                                    if (partialRefundSuccess)
                                    {
                                        refundedCount++;
                                        totalRefunded += esc.GrossAmount * refundPercentage;
                                    }
                                    else
                                    {
                                        throw new InvalidOperationException($"Không thể hoàn tiền escrow {esc.Id}");
                                    }
                                }
                            }
                        }

                        // Forfeit deposit về ví admin
                        if (tutorDeposit != null && tutorDeposit.Status == TutorDepositStatus.Held)
                        {
                            var forfeitSuccess = await ForfeitDepositInTransactionAsync(adminUserId, tutorDeposit, false, $"Admin hủy lớp do lỗi tutor (đã dạy {completedLessons}/{totalLessons} buổi): {request.Note}");
                            depositRefunded = forfeitSuccess;
                            if (depositRefunded)
                            {
                                depositRefundAmount = tutorDeposit.DepositAmount;
                            }
                        }
                    }
                    break;

                case ClassCancelReason.StudentFault:
                    // HS lỗi
                    // Không refund HS, trả tutor theo buổi đã dạy, hoàn deposit
                    if (hasStartedTeaching)
                    {
                        // Đã dạy → Partial release cho tutor theo % đã dạy (dùng biến đã khai báo ở scope ngoài)
                        decimal studentFaultTeachingPercentage = totalLessons > 0 
                            ? (decimal)completedLessons / totalLessons 
                            : 0;
                        
                        foreach (var esc in escrows)
                        {
                            if (esc.Status == EscrowStatus.Held || esc.Status == EscrowStatus.PartiallyReleased)
                            {
                                var releaseSuccess = await PartialReleaseEscrowInTransactionAsync(adminUserId, esc, studentFaultTeachingPercentage);
                                if (!releaseSuccess)
                                {
                                    throw new InvalidOperationException($"Không thể giải ngân escrow {esc.Id}");
                                }
                            }
                        }
                    }

                    // Hoàn deposit cho tutor (tutor không lỗi)
                    if (tutorDeposit != null && tutorDeposit.Status == TutorDepositStatus.Held)
                    {
                        var refundDepositResult = await RefundTutorDepositAsync(adminUserId, tutorDeposit, $"Admin hủy lớp do lỗi học sinh: {request.Note}");
                        if (refundDepositResult)
                        {
                            depositRefunded = true;
                            depositRefundAmount = tutorDeposit.DepositAmount;
                        }
                    }
                    break;

                case ClassCancelReason.MutualConsent:
                    // Hai bên đồng ý
                    // Trả tutor theo buổi, refund 80% phần còn lại cho HS
                    if (hasStartedTeaching)
                    {
                        // Đã dạy → Partial release cho tutor theo % đã dạy (dùng biến đã khai báo ở scope ngoài)
                        decimal mutualConsentTeachingPercentage = totalLessons > 0 
                            ? (decimal)completedLessons / totalLessons 
                            : 0;
                        
                        foreach (var esc in escrows)
                        {
                            if (esc.Status == EscrowStatus.Held || esc.Status == EscrowStatus.PartiallyReleased)
                            {
                                // Release cho tutor theo % đã dạy
                                var releaseSuccess = await PartialReleaseEscrowInTransactionAsync(adminUserId, esc, mutualConsentTeachingPercentage);
                                if (!releaseSuccess)
                                {
                                    throw new InvalidOperationException($"Không thể giải ngân escrow {esc.Id}");
                                }
                                
                                // Refund 80% phần còn lại cho HS
                                decimal remainingPercentage = 1.0m - mutualConsentTeachingPercentage;
                                if (remainingPercentage > 0)
                                {
                                    decimal refundPercentage = remainingPercentage * 0.8m; // 80% của phần còn lại
                                    var partialRefundSuccess = await PartialRefundEscrowInTransactionAsync(adminUserId, esc, refundPercentage);
                                    if (partialRefundSuccess)
                                    {
                                        refundedCount++;
                                        totalRefunded += esc.GrossAmount * refundPercentage;
                                    }
                                    else
                                    {
                                        throw new InvalidOperationException($"Không thể hoàn tiền escrow {esc.Id}");
                                    }
                                }
                            }
                        }
                    }
                    else
                    {
                        // Chưa dạy → Refund 80% cho HS, không trả tutor
                        foreach (var esc in escrows)
                        {
                            var partialRefundSuccess = await PartialRefundEscrowInTransactionAsync(adminUserId, esc, 0.8m);
                            if (partialRefundSuccess)
                            {
                                refundedCount++;
                                totalRefunded += esc.GrossAmount * 0.8m;
                            }
                            else
                            {
                                throw new InvalidOperationException($"Không thể hoàn tiền escrow {esc.Id}");
                            }
                        }
                    }

                    // Hoàn deposit (thường 100%)
                    if (tutorDeposit != null && tutorDeposit.Status == TutorDepositStatus.Held)
                    {
                        var refundDepositResult = await RefundTutorDepositAsync(adminUserId, tutorDeposit, $"Admin hủy lớp theo thỏa thuận: {request.Note}");
                        if (refundDepositResult)
                        {
                            depositRefunded = true;
                            depositRefundAmount = tutorDeposit.DepositAmount;
                        }
                    }
                    break;

                case ClassCancelReason.Other:
                    // Lý do khác → Xử lý như SystemError (refund full để đảm bảo công bằng)
                    // Refund tất cả escrow
                    foreach (var esc in escrows)
                    {
                        var escrowId = esc.Id;
                        var escrowStatus = esc.Status;
                        var grossAmount = esc.GrossAmount;
                        // Tính số tiền sẽ refund: Held = full, PartiallyReleased = phần còn lại
                        decimal expectedRefundAmount = escrowStatus == EscrowStatus.Held 
                            ? grossAmount 
                            : (grossAmount - esc.ReleasedAmount - esc.RefundedAmount);
                        
                        var refundSuccess = await RefundEscrowInTransactionAsync(adminUserId, esc);
                        if (refundSuccess)
                        {
                            refundedCount++;
                            totalRefunded += expectedRefundAmount;
                            
                            // Reload escrow để lưu vào list
                            var escForNotification = await _mainUow.Escrows.GetByIdAsync(escrowId);
                            if (escForNotification != null)
                            {
                                refundedEscrows.Add(escForNotification);
                            }
                        }
                        else
                        {
                            Console.WriteLine($"Lỗi refund escrow {escrowId} (Other)");
                            throw new InvalidOperationException($"Không thể hoàn tiền escrow {escrowId}");
                        }
                    }

                    // Refund deposit nếu có (hoàn 100% cho tutor)
                    if (tutorDeposit != null && tutorDeposit.Status == TutorDepositStatus.Held)
                    {
                        var refundDepositResult = await RefundTutorDepositAsync(adminUserId, tutorDeposit, $"Admin hủy lớp - Lý do khác: {request.Note}");
                        if (refundDepositResult)
                        {
                            depositRefunded = true;
                            depositRefundAmount = tutorDeposit.DepositAmount;
                        }
                    }
                    break;
            }

            // Cập nhật trạng thái lớp
            classEntity.Status = ClassStatus.Cancelled;
            await _uow.Classes.UpdateAsync(classEntity);

            // Xóa TẤT CẢ lessons và schedule entries (hard delete để tránh trùng lịch)
            var allLessons = await _context.Lessons
                .Where(l => l.ClassId == request.ClassId && l.DeletedAt == null)
                .ToListAsync();

            if (allLessons.Any())
            {
                var lessonIds = allLessons.Select(l => l.Id).ToList();
                var allScheduleEntries = await _context.ScheduleEntries
                    .Where(se => lessonIds.Contains(se.LessonId) && se.DeletedAt == null)
                    .ToListAsync();

                // Hard delete tất cả schedule entries trước (vì có foreign key)
                _context.ScheduleEntries.RemoveRange(allScheduleEntries);
                
                // Hard delete tất cả lessons
                _context.Lessons.RemoveRange(allLessons);
                
                Console.WriteLine($"[CancelClassByAdminAsync] Đã xóa vĩnh viễn {allLessons.Count} lessons và {allScheduleEntries.Count} schedule entries của lớp {request.ClassId}");
            }

            // Cập nhật ClassAssign status
            var classAssigns = await _uow.ClassAssigns.GetByClassIdAsync(request.ClassId);
            foreach (var assign in classAssigns)
            {
                // Có thể thêm field CancelledAt nếu cần
            }

            await _uow.SaveChangesAsync();
            await _mainUow.SaveChangesAsync();
            await tx.CommitAsync();

            // ========== GỬI NOTIFICATION CHI TIẾT CHO HỌC SINH VÀ GIA SƯ ==========
            
            // Lấy thông tin tutor
            var tutorProfile = await _mainUow.TutorProfiles.GetByIdAsync(classEntity.TutorId);
            string tutorUserId = tutorProfile?.UserId ?? string.Empty;
            
            // Tính toán thông tin để gửi notification
            decimal teachingPercentage = totalLessons > 0 
                ? (decimal)completedLessons / totalLessons 
                : 0;
            
            // Tạo message chi tiết cho từng reason
            string studentRefundMessage = GetStudentRefundMessage(request.Reason, totalRefunded, refundedCount, hasStartedTeaching, teachingPercentage, request.Note);
            string tutorDepositMessage = GetTutorDepositMessage(request.Reason, depositRefunded, depositRefundAmount, hasStartedTeaching, teachingPercentage, request.Note);
            
            // 1. Gửi notification cho HỌC SINH về việc hủy lớp và hoàn tiền
            var allStudentUserIdsForNotification = escrows.Select(e => e.StudentUserId).Distinct().ToList();
            foreach (var studentUserId in allStudentUserIdsForNotification)
            {
                try
                {
                    // Notification về việc hủy lớp
                    var cancelNotification = await _notificationService.CreateAccountNotificationAsync(
                        studentUserId,
                        NotificationType.ClassCancelled,
                        $"Lớp học đã bị hủy bởi quản trị viên. {studentRefundMessage}",
                        request.ClassId);
                    await _uow.SaveChangesAsync();
                    await _notificationService.SendRealTimeNotificationAsync(studentUserId, cancelNotification);
                    
                    // Notification về việc hoàn tiền (nếu có)
                    var studentEscrow = refundedEscrows.FirstOrDefault(e => e.StudentUserId == studentUserId);
                    if (studentEscrow != null)
                    {
                        decimal refundAmount = studentEscrow.RefundedAmount > 0 
                            ? studentEscrow.RefundedAmount 
                            : studentEscrow.GrossAmount;
                        
                        var refundNotification = await _notificationService.CreateEscrowNotificationAsync(
                            studentUserId,
                            NotificationType.EscrowRefunded,
                            refundAmount,
                            request.ClassId,
                            studentEscrow.Id);
                        await _mainUow.SaveChangesAsync();
                        await _notificationService.SendRealTimeNotificationAsync(studentUserId, refundNotification);
                        
                        Console.WriteLine($"CancelClassByAdminAsync: Đã gửi notification hoàn tiền cho student {studentUserId}, số tiền: {refundAmount:N0} VND");
                    }
                }
                catch (Exception notifEx)
                {
                    Console.WriteLine($"CancelClassByAdminAsync: Lỗi gửi notification cho student {studentUserId}: {notifEx.Message}");
                }
            }
            
            // 2. Gửi notification cho GIA SƯ về việc hủy lớp và hoàn tiền cọc
            if (!string.IsNullOrEmpty(tutorUserId))
            {
                try
                {
                    // Notification về việc hủy lớp
                    var tutorCancelNotification = await _notificationService.CreateAccountNotificationAsync(
                        tutorUserId,
                        NotificationType.ClassCancelled,
                        $"Lớp học đã bị hủy bởi quản trị viên. {tutorDepositMessage}",
                        request.ClassId);
                    await _uow.SaveChangesAsync();
                    await _notificationService.SendRealTimeNotificationAsync(tutorUserId, tutorCancelNotification);
                    
                    // Notification về việc hoàn tiền cọc (nếu có)
                    if (depositRefunded && depositRefundAmount.HasValue)
                    {
                        var depositNotification = await _notificationService.CreateAccountNotificationAsync(
                            tutorUserId,
                            NotificationType.TutorDepositRefunded,
                            $"Bạn đã nhận hoàn tiền cọc {depositRefundAmount.Value:N0} VND. {tutorDepositMessage}",
                            request.ClassId);
                        await _uow.SaveChangesAsync();
                        await _notificationService.SendRealTimeNotificationAsync(tutorUserId, depositNotification);
                        
                        Console.WriteLine($"CancelClassByAdminAsync: Đã gửi notification hoàn cọc cho tutor {tutorUserId}, số tiền: {depositRefundAmount.Value:N0} VND");
                    }
                    // Notification về việc tịch thu tiền cọc (nếu có)
                    else if (request.Reason == ClassCancelReason.TutorFault && tutorDeposit != null)
                    {
                        var forfeitNotification = await _notificationService.CreateAccountNotificationAsync(
                            tutorUserId,
                            NotificationType.TutorDepositForfeited,
                            $"Tiền cọc {tutorDeposit.DepositAmount:N0} VND đã bị tịch thu do lỗi của bạn. {tutorDepositMessage}",
                            request.ClassId);
                        await _uow.SaveChangesAsync();
                        await _notificationService.SendRealTimeNotificationAsync(tutorUserId, forfeitNotification);
                        
                        Console.WriteLine($"CancelClassByAdminAsync: Đã gửi notification tịch thu cọc cho tutor {tutorUserId}, số tiền: {tutorDeposit.DepositAmount:N0} VND");
                    }
                }
                catch (Exception notifEx)
                {
                    Console.WriteLine($"CancelClassByAdminAsync: Lỗi gửi notification cho tutor {tutorUserId}: {notifEx.Message}");
                }
            }

            // Xóa conversation của lớp khi hủy
            try
            {
                await _conversationService.DeleteClassConversationAsync(request.ClassId);
            }
            catch (Exception convEx)
            {
                Console.WriteLine($"CancelClassByAdminAsync: Lỗi khi xóa conversation: {convEx.Message}");
            }

            return new CancelClassResponseDto
            {
                ClassId = request.ClassId,
                NewStatus = ClassStatus.Cancelled,
                Reason = request.Reason,
                RefundedEscrowsCount = refundedCount,
                TotalRefundedAmount = totalRefunded,
                DepositRefunded = depositRefunded,
                DepositRefundAmount = depositRefundAmount,
                Message = $"Đã hủy lớp. Đã hoàn {refundedCount} escrow, tổng {totalRefunded:N0} VND."
            };
        }

        /// <summary>
        /// Admin hủy 1 học sinh khỏi lớp group
        /// </summary>
        /// <summary>
        /// Admin hủy 1 học sinh khỏi lớp (group class)
        /// </summary>
        public async Task<CancelClassResponseDto> CancelStudentEnrollmentAsync(string adminUserId, string classId, string studentId, ClassCancelReason reason, string? note)
        {
            using var tx = await _mainUow.BeginTransactionAsync();
            int refundedCount = 0;
            decimal totalRefunded = 0;

            try
            {
                var classEntity = await _uow.Classes.GetByIdAsync(classId);
                if (classEntity == null)
                    throw new KeyNotFoundException("Không tìm thấy lớp học.");

                if (classEntity.StudentLimit == 1)
                    throw new InvalidOperationException("Không thể hủy học sinh khỏi lớp 1-1. Hãy sử dụng CancelClassByAdminAsync.");

                // Lấy ClassAssign
                var classAssign = await _mainUow.ClassAssigns.GetByClassAndStudentAsync(classId, studentId);
                if (classAssign == null)
                    throw new KeyNotFoundException("Học sinh chưa ghi danh vào lớp học này.");

                // Lấy Escrow của học sinh này
                var escrows = await _mainUow.Escrows.GetAllAsync(
                    filter: e => e.ClassAssignId == classAssign.Id && 
                                 (e.Status == EscrowStatus.Held || e.Status == EscrowStatus.PartiallyReleased));

                // Đảm bảo wallets đã tồn tại trước khi vào transaction
                var adminWalletForStudent = await _mainUow.Wallets.GetByUserIdAsync(_systemWalletOptions.SystemWalletUserId);
                if (adminWalletForStudent == null)
                {
                    adminWalletForStudent = new Wallet
                    {
                        UserId = _systemWalletOptions.SystemWalletUserId,
                        Balance = 0,
                        Currency = "VND",
                        IsFrozen = false
                    };
                    await _mainUow.Wallets.AddAsync(adminWalletForStudent);
                    await _mainUow.SaveChangesAsync();
                }

                var studentUserIdsForRefund = escrows.Select(e => e.StudentUserId).Distinct().ToList();
                foreach (var studentUserId in studentUserIdsForRefund)
                {
                    var studentWallet = await _mainUow.Wallets.GetByUserIdAsync(studentUserId);
                    if (studentWallet == null)
                    {
                        studentWallet = new Wallet
                        {
                            UserId = studentUserId,
                            Balance = 0,
                            Currency = "VND",
                            IsFrozen = false
                        };
                        await _mainUow.Wallets.AddAsync(studentWallet);
                    }
                }
                await _mainUow.SaveChangesAsync();

                // Refund escrow cho học sinh
                foreach (var esc in escrows)
                {
                    if (esc.Status == EscrowStatus.Held)
                    {
                        // Refund full
                        var refundSuccess = await RefundEscrowInTransactionAsync(adminUserId, esc);
                        if (refundSuccess)
                        {
                            refundedCount++;
                            totalRefunded += esc.GrossAmount;
                        }
                        else
                        {
                            throw new InvalidOperationException($"Không thể hoàn tiền escrow {esc.Id}");
                        }
                    }
                    else if (esc.Status == EscrowStatus.PartiallyReleased)
                    {
                        // Refund phần còn lại
                        decimal remainingPercentage = 1.0m - (esc.ReleasedAmount / esc.GrossAmount);
                        if (remainingPercentage > 0)
                        {
                            var partialRefundSuccess = await PartialRefundEscrowInTransactionAsync(adminUserId, esc, remainingPercentage);
                            
                            if (partialRefundSuccess)
                            {
                                refundedCount++;
                                totalRefunded += esc.GrossAmount * remainingPercentage;
                            }
                            else
                            {
                                throw new InvalidOperationException($"Không thể hoàn tiền escrow {esc.Id}");
                            }
                        }
                    }
                }

                // Cập nhật ClassAssign
                classAssign.PaymentStatus = PaymentStatus.Refunded;
                classAssign.ApprovalStatus = ApprovalStatus.Pending; // Hoặc có thể thêm Rejected vào enum nếu cần
                await _mainUow.ClassAssigns.UpdateAsync(classAssign);

                // Tính lại CurrentStudentCount dựa trên số ClassAssign thực tế có PaymentStatus = Paid
                // Đảm bảo tính chính xác, tránh lỗi khi có học sinh bị hủy hoặc có vấn đề với transaction
                var actualPaidCount = await _context.ClassAssigns
                    .CountAsync(ca => ca.ClassId == classId && ca.PaymentStatus == PaymentStatus.Paid);
                classEntity.CurrentStudentCount = actualPaidCount;
                await _uow.Classes.UpdateAsync(classEntity);

                await _uow.SaveChangesAsync();
                await _mainUow.SaveChangesAsync();
                await tx.CommitAsync();

                // Gửi notification cho học sinh
                var studentProfile = await _mainUow.StudentProfiles.GetByIdAsync(studentId);
                if (studentProfile != null && !string.IsNullOrEmpty(studentProfile.UserId))
                {
                    string reasonMessage = GetCancelReasonMessage(reason, note);
                    var studentNotification = await _notificationService.CreateAccountNotificationAsync(
                        studentProfile.UserId,
                        NotificationType.ClassCancelled,
                        $"Bạn đã bị hủy khỏi lớp học. {reasonMessage}",
                        classId);
                    await _uow.SaveChangesAsync();
                    await _notificationService.SendRealTimeNotificationAsync(studentProfile.UserId, studentNotification);

                    // Xóa participant khỏi conversation (không xóa conversation vì lớp vẫn còn học sinh khác)
                    try
                    {
                        await _conversationService.RemoveParticipantFromClassConversationAsync(classId, studentProfile.UserId);
                    }
                    catch (Exception convEx)
                    {
                        Console.WriteLine($"CancelStudentEnrollmentAsync: Lỗi khi xóa participant khỏi conversation: {convEx.Message}");
                    }
                }

                return new CancelClassResponseDto
                {
                    ClassId = classId,
                    NewStatus = classEntity.Status ?? ClassStatus.Cancelled,
                    Reason = reason,
                    RefundedEscrowsCount = refundedCount,
                    TotalRefundedAmount = totalRefunded,
                    Message = $"Đã hủy học sinh khỏi lớp. Đã hoàn {refundedCount} escrow, tổng {totalRefunded:N0} VND."
                };
            }
            catch (Exception ex)
            {
                await tx.RollbackAsync();
                throw;
            }
        }

        #endregion

        #region Private Helpers

        /// <summary>
        /// Helper: Refund escrow (sử dụng _mainUow để đảm bảo cùng transaction với CancelClassByAdminAsync)
        /// Hỗ trợ cả Held (refund full) và PartiallyReleased (refund phần còn lại)
        /// </summary>
        private async Task<bool> RefundEscrowInTransactionAsync(string adminUserId, Escrow esc, CancellationToken ct = default)
        {
            // Reload escrow trong transaction để đảm bảo có status mới nhất
            var escReloaded = await _mainUow.Escrows.GetByIdAsync(esc.Id, ct);
            if (escReloaded == null)
            {
                Console.WriteLine($"RefundEscrowInTransactionAsync: Escrow {esc.Id} không tồn tại trong database");
                return false;
            }

            // Sử dụng escReloaded thay vì esc
            esc = escReloaded;

            decimal refundAmount;
            if (esc.Status == EscrowStatus.Held)
            {
                // Refund full
                refundAmount = esc.GrossAmount;
                Console.WriteLine($"RefundEscrowInTransactionAsync: Bắt đầu refund FULL escrow {esc.Id}, số tiền: {refundAmount:N0} VND cho student {esc.StudentUserId}");
            }
            else if (esc.Status == EscrowStatus.PartiallyReleased)
            {
                // Refund phần còn lại (chưa release, chưa refund)
                decimal remainingAmount = esc.GrossAmount - esc.ReleasedAmount - esc.RefundedAmount;
                if (remainingAmount <= 0)
                {
                    Console.WriteLine($"RefundEscrowInTransactionAsync: Escrow {esc.Id} không còn tiền để refund (đã release/refund hết)");
                    return false;
                }
                refundAmount = remainingAmount;
                Console.WriteLine($"RefundEscrowInTransactionAsync: Bắt đầu refund PHẦN CÒN LẠI escrow {esc.Id}, số tiền: {refundAmount:N0} VND (còn lại trong tổng {esc.GrossAmount:N0}) cho student {esc.StudentUserId}");
            }
            else
            {
                Console.WriteLine($"RefundEscrowInTransactionAsync: Escrow {esc.Id} có status {esc.Status}, không thể refund (cần Held hoặc PartiallyReleased)");
                return false;
            }

            // Get wallets (đã được tạo trước khi vào transaction)
            var adminWallet = await _mainUow.Wallets.GetByUserIdAsync(_systemWalletOptions.SystemWalletUserId, ct);
            if (adminWallet == null)
            {
                Console.WriteLine($"RefundEscrowInTransactionAsync: Admin wallet không tồn tại sau khi đã tạo trước transaction");
                return false;
            }

            var studentWallet = await _mainUow.Wallets.GetByUserIdAsync(esc.StudentUserId, ct);
            if (studentWallet == null)
            {
                Console.WriteLine($"RefundEscrowInTransactionAsync: Student wallet không tồn tại sau khi đã tạo trước transaction");
                return false;
            }

            Console.WriteLine($"RefundEscrowInTransactionAsync: Kiểm tra số dư - Admin balance: {adminWallet.Balance:N0}, Cần refund: {refundAmount:N0}");
            if (adminWallet.Balance < refundAmount)
            {
                Console.WriteLine($"RefundEscrowInTransactionAsync: ❌ Số dư admin không đủ. Số dư: {adminWallet.Balance:N0}, Cần: {refundAmount:N0}");
                return false;
            }

            // Transfer money
            Console.WriteLine($"RefundEscrowInTransactionAsync: Bắt đầu chuyển tiền - Admin: {adminWallet.Balance:N0} → {adminWallet.Balance - refundAmount:N0}, Student: {studentWallet.Balance:N0} → {studentWallet.Balance + refundAmount:N0}");
            adminWallet.Balance -= refundAmount;
            studentWallet.Balance += refundAmount;

            await _mainUow.Wallets.Update(adminWallet);
            await _mainUow.Wallets.Update(studentWallet);

            // Create transactions
            await _mainUow.Transactions.AddAsync(new Transaction
            {
                WalletId = adminWallet.Id,
                Type = TransactionType.RefundOut,
                Status = TransactionStatus.Succeeded,
                Amount = -refundAmount,
                Note = esc.Status == EscrowStatus.Held 
                    ? $"Refund escrow {esc.Id}" 
                    : $"Partial refund escrow {esc.Id} (phần còn lại)",
                CounterpartyUserId = esc.StudentUserId
            }, ct);

            await _mainUow.Transactions.AddAsync(new Transaction
            {
                WalletId = studentWallet.Id,
                Type = TransactionType.RefundIn,
                Status = TransactionStatus.Succeeded,
                Amount = refundAmount,
                Note = esc.Status == EscrowStatus.Held 
                    ? $"Refund received for escrow {esc.Id}" 
                    : $"Partial refund received for escrow {esc.Id} (phần còn lại)",
                CounterpartyUserId = adminUserId
            }, ct);

            // Update escrow
            esc.RefundedAmount = esc.RefundedAmount + refundAmount;
            if (esc.RefundedAmount + esc.ReleasedAmount >= esc.GrossAmount)
            {
                // Đã refund/release hết → chuyển sang Refunded
                esc.Status = EscrowStatus.Refunded;
                esc.RefundedAt = DateTimeHelper.VietnamNow;
            }
            // Nếu chưa hết, giữ nguyên status PartiallyReleased
            await _mainUow.Escrows.UpdateAsync(esc);

            // KHÔNG gọi SaveChanges ở đây - để CancelClassByAdminAsync gọi một lần ở cuối transaction
            // Tất cả thay đổi (wallet balance, transactions, escrow) đều được track bởi Entity Framework
            // và sẽ được lưu khi transaction commit

            Console.WriteLine($"RefundEscrowInTransactionAsync: ✅ Đã chuẩn bị refund escrow {esc.Id}, số tiền: {refundAmount:N0} VND cho student {esc.StudentUserId}");
            Console.WriteLine($"RefundEscrowInTransactionAsync: Admin wallet balance (sẽ giảm): {adminWallet.Balance + refundAmount:N0} → {adminWallet.Balance:N0}");
            Console.WriteLine($"RefundEscrowInTransactionAsync: Student wallet balance (sẽ tăng): {studentWallet.Balance - refundAmount:N0} → {studentWallet.Balance:N0}");
            return true;
        }

        /// <summary>
        /// Helper: Partial refund escrow trong transaction (không gọi SaveChanges)
        /// </summary>
        private async Task<bool> PartialRefundEscrowInTransactionAsync(string adminUserId, Escrow esc, decimal refundPercentage, CancellationToken ct = default)
        {
            // Reload escrow trong transaction để đảm bảo có status mới nhất
            var escReloaded = await _mainUow.Escrows.GetByIdAsync(esc.Id, ct);
            if (escReloaded == null)
            {
                Console.WriteLine($"PartialRefundEscrowInTransactionAsync: Escrow {esc.Id} không tồn tại trong database");
                return false;
            }

            esc = escReloaded;

            if (esc.Status != EscrowStatus.Held && esc.Status != EscrowStatus.PartiallyReleased)
            {
                Console.WriteLine($"PartialRefundEscrowInTransactionAsync: Escrow {esc.Id} có status {esc.Status}, không thể refund");
                return false;
            }

            if (refundPercentage <= 0 || refundPercentage > 1)
            {
                Console.WriteLine($"PartialRefundEscrowInTransactionAsync: RefundPercentage {refundPercentage} không hợp lệ");
                return false;
            }

            // Tính số tiền còn lại có thể refund (chưa release)
            decimal remainingAmount = esc.GrossAmount - esc.ReleasedAmount - esc.RefundedAmount;
            decimal refundAmount = Math.Round(remainingAmount * refundPercentage, 2, MidpointRounding.AwayFromZero);

            if (refundAmount <= 0)
            {
                Console.WriteLine($"PartialRefundEscrowInTransactionAsync: Không còn tiền để refund");
                return false;
            }

            // Get wallets (đã được tạo trước khi vào transaction)
            var adminWallet = await _mainUow.Wallets.GetByUserIdAsync(_systemWalletOptions.SystemWalletUserId, ct);
            if (adminWallet == null)
            {
                Console.WriteLine($"PartialRefundEscrowInTransactionAsync: Admin wallet không tồn tại sau khi đã tạo trước transaction");
                return false;
            }

            var studentWallet = await _mainUow.Wallets.GetByUserIdAsync(esc.StudentUserId, ct);
            if (studentWallet == null)
            {
                Console.WriteLine($"PartialRefundEscrowInTransactionAsync: Student wallet không tồn tại sau khi đã tạo trước transaction");
                return false;
            }

            if (adminWallet.Balance < refundAmount)
            {
                Console.WriteLine($"PartialRefundEscrowInTransactionAsync: ❌ Số dư admin không đủ. Số dư: {adminWallet.Balance:N0}, Cần: {refundAmount:N0}");
                return false;
            }

            // Transfer money
            adminWallet.Balance -= refundAmount;
            studentWallet.Balance += refundAmount;

            await _mainUow.Wallets.Update(adminWallet);
            await _mainUow.Wallets.Update(studentWallet);

            // Create transactions
            await _mainUow.Transactions.AddAsync(new Transaction
            {
                WalletId = adminWallet.Id,
                Type = TransactionType.RefundOut,
                Status = TransactionStatus.Succeeded,
                Amount = -refundAmount,
                Note = $"Partial refund {refundPercentage:P0} for escrow {esc.Id}",
                CounterpartyUserId = esc.StudentUserId
            }, ct);

            await _mainUow.Transactions.AddAsync(new Transaction
            {
                WalletId = studentWallet.Id,
                Type = TransactionType.RefundIn,
                Status = TransactionStatus.Succeeded,
                Amount = refundAmount,
                Note = $"Partial refund received for escrow {esc.Id}",
                CounterpartyUserId = adminUserId
            }, ct);

            // Update escrow
            esc.RefundedAmount += refundAmount;
            if (esc.RefundedAmount + esc.ReleasedAmount >= esc.GrossAmount)
            {
                esc.Status = EscrowStatus.Refunded;
                esc.RefundedAt = DateTimeHelper.VietnamNow;
            }
            await _mainUow.Escrows.UpdateAsync(esc);

            // KHÔNG gọi SaveChanges ở đây
            return true;
        }

        /// <summary>
        /// Helper: Partial release escrow trong transaction (không gọi SaveChanges)
        /// </summary>
        private async Task<bool> PartialReleaseEscrowInTransactionAsync(string adminUserId, Escrow esc, decimal releasePercentage, CancellationToken ct = default)
        {
            // Reload escrow trong transaction để đảm bảo có status mới nhất
            var escReloaded = await _mainUow.Escrows.GetByIdAsync(esc.Id, ct);
            if (escReloaded == null)
            {
                Console.WriteLine($"PartialReleaseEscrowInTransactionAsync: Escrow {esc.Id} không tồn tại trong database");
                return false;
            }

            esc = escReloaded;

            if (esc.Status != EscrowStatus.Held && esc.Status != EscrowStatus.PartiallyReleased)
            {
                Console.WriteLine($"PartialReleaseEscrowInTransactionAsync: Escrow {esc.Id} có status {esc.Status}, không thể release");
                return false;
            }

            if (releasePercentage <= 0 || releasePercentage > 1)
            {
                Console.WriteLine($"PartialReleaseEscrowInTransactionAsync: ReleasePercentage {releasePercentage} không hợp lệ");
                return false;
            }

            // Tính số tiền còn lại có thể release
            decimal remainingAmount = esc.GrossAmount - esc.ReleasedAmount;
            decimal releaseAmount = Math.Round(remainingAmount * releasePercentage, 2, MidpointRounding.AwayFromZero);

            if (releaseAmount <= 0)
            {
                Console.WriteLine($"PartialReleaseEscrowInTransactionAsync: Không còn tiền để release");
                return false;
            }

            // Tính commission và net
            var commission = Math.Round(releaseAmount * esc.CommissionRateSnapshot, 2, MidpointRounding.AwayFromZero);
            var net = releaseAmount - commission;

            // Get wallets (đã được tạo trước khi vào transaction)
            var adminWallet = await _mainUow.Wallets.GetByUserIdAsync(_systemWalletOptions.SystemWalletUserId, ct);
            if (adminWallet == null)
            {
                Console.WriteLine($"PartialReleaseEscrowInTransactionAsync: Admin wallet không tồn tại sau khi đã tạo trước transaction");
                return false;
            }

            var tutorWallet = await _mainUow.Wallets.GetByUserIdAsync(esc.TutorUserId, ct);
            if (tutorWallet == null)
            {
                Console.WriteLine($"PartialReleaseEscrowInTransactionAsync: Tutor wallet không tồn tại sau khi đã tạo trước transaction");
                return false;
            }

            if (adminWallet.Balance < net)
            {
                Console.WriteLine($"PartialReleaseEscrowInTransactionAsync: ❌ Số dư admin không đủ. Số dư: {adminWallet.Balance:N0}, Cần: {net:N0}");
                return false;
            }

            // Transfer money
            adminWallet.Balance -= net;
            tutorWallet.Balance += net;

            await _mainUow.Wallets.Update(adminWallet);
            await _mainUow.Wallets.Update(tutorWallet);

            // Create transactions
            await _mainUow.Transactions.AddAsync(new Transaction
            {
                WalletId = adminWallet.Id,
                Type = TransactionType.PayoutOut,
                Status = TransactionStatus.Succeeded,
                Amount = -net,
                Note = $"Partial release {releasePercentage:P0} for escrow {esc.Id}",
                CounterpartyUserId = esc.TutorUserId
            }, ct);

            await _mainUow.Transactions.AddAsync(new Transaction
            {
                WalletId = tutorWallet.Id,
                Type = TransactionType.PayoutIn,
                Status = TransactionStatus.Succeeded,
                Amount = net,
                Note = $"Partial payout received for escrow {esc.Id}",
                CounterpartyUserId = adminUserId
            }, ct);

            // Ghi commission
            await _mainUow.Transactions.AddAsync(new Transaction
            {
                WalletId = adminWallet.Id,
                Type = TransactionType.Commission,
                Status = TransactionStatus.Succeeded,
                Amount = commission,
                Note = $"Commission from partial release escrow {esc.Id}",
                CounterpartyUserId = esc.TutorUserId
            }, ct);

            // Update escrow
            esc.ReleasedAmount += releaseAmount;
            if (esc.ReleasedAmount >= esc.GrossAmount)
            {
                esc.Status = EscrowStatus.Released;
            }
            else
            {
                esc.Status = EscrowStatus.PartiallyReleased;
            }
            await _mainUow.Escrows.UpdateAsync(esc);

            // KHÔNG gọi SaveChanges ở đây
            return true;
        }

        /// <summary>
        /// Helper: Forfeit deposit trong transaction (không gọi SaveChanges)
        /// </summary>
        private async Task<bool> ForfeitDepositInTransactionAsync(string adminUserId, TutorDepositEscrow deposit, bool refundToStudent, string reason, CancellationToken ct = default)
        {
            if (deposit.Status != TutorDepositStatus.Held)
            {
                Console.WriteLine($"ForfeitDepositInTransactionAsync: Deposit {deposit.Id} có status {deposit.Status}, không thể forfeit");
                return false;
            }

            // Reload deposit trong transaction
            var depositReloaded = await _mainUow.TutorDepositEscrows.GetByIdAsync(deposit.Id, ct);
            if (depositReloaded == null)
            {
                Console.WriteLine($"ForfeitDepositInTransactionAsync: Deposit {deposit.Id} không tồn tại trong database");
                return false;
            }

            deposit = depositReloaded;

            var adminWallet = await _mainUow.Wallets.GetByUserIdAsync(_systemWalletOptions.SystemWalletUserId, ct);
            if (adminWallet == null)
            {
                Console.WriteLine($"ForfeitDepositInTransactionAsync: Admin wallet không tồn tại sau khi đã tạo trước transaction");
                return false;
            }

            var depositAmount = deposit.DepositAmount;

            if (refundToStudent)
            {
                // Trả về học sinh - với lớp group, chia đều cho tất cả học sinh đã thanh toán
                var paidEscrows = await _mainUow.Escrows.GetAllAsync(
                    filter: e => e.ClassId == deposit.ClassId && e.Status == EscrowStatus.Held);

                if (!paidEscrows.Any())
                {
                    Console.WriteLine($"ForfeitDepositInTransactionAsync: Không tìm thấy học sinh đã thanh toán để trả tiền");
                    return false;
                }

                // Chia đều depositAmount cho tất cả học sinh đã thanh toán
                decimal refundPerStudent = Math.Round(depositAmount / paidEscrows.Count(), 2, MidpointRounding.AwayFromZero);

                if (adminWallet.Balance < depositAmount)
                {
                    Console.WriteLine($"ForfeitDepositInTransactionAsync: ❌ Số dư admin không đủ. Số dư: {adminWallet.Balance:N0}, Cần: {depositAmount:N0}");
                    return false;
                }

                foreach (var esc in paidEscrows)
                {
                    var studentWallet = await _mainUow.Wallets.GetByUserIdAsync(esc.StudentUserId, ct);
                    if (studentWallet == null)
                    {
                        Console.WriteLine($"ForfeitDepositInTransactionAsync: Student wallet {esc.StudentUserId} không tồn tại sau khi đã tạo trước transaction");
                        continue;
                    }

                    adminWallet.Balance -= refundPerStudent;
                    studentWallet.Balance += refundPerStudent;

                    await _mainUow.Wallets.Update(studentWallet);

                    await _mainUow.Transactions.AddAsync(new Transaction
                    {
                        WalletId = adminWallet.Id,
                        Type = TransactionType.DepositForfeitOut,
                        Status = TransactionStatus.Succeeded,
                        Amount = -refundPerStudent,
                        Note = $"Tịch thu cọc và trả cho học sinh: {reason}",
                        CounterpartyUserId = esc.StudentUserId
                    }, ct);

                    await _mainUow.Transactions.AddAsync(new Transaction
                    {
                        WalletId = studentWallet.Id,
                        Type = TransactionType.DepositForfeitIn,
                        Status = TransactionStatus.Succeeded,
                        Amount = refundPerStudent,
                        Note = $"Nhận tiền bồi thường từ cọc gia sư: {reason}",
                        CounterpartyUserId = adminUserId
                    }, ct);
                }

                await _mainUow.Wallets.Update(adminWallet);
            }
            else
            {
                // Giữ lại cho hệ thống (phí vi phạm)
                // Không cần chuyển tiền, chỉ ghi transaction
                await _mainUow.Transactions.AddAsync(new Transaction
                {
                    WalletId = adminWallet.Id,
                    Type = TransactionType.Commission, // Hoặc tạo type mới: ViolationFee
                    Status = TransactionStatus.Succeeded,
                    Amount = depositAmount,
                    Note = $"Tịch thu cọc do vi phạm: {reason}",
                    CounterpartyUserId = deposit.TutorUserId
                }, ct);
            }

            // Cập nhật trạng thái
            deposit.Status = TutorDepositStatus.Forfeited;
            deposit.ForfeitedAt = DateTimeHelper.VietnamNow;
            deposit.ForfeitReason = reason;

            await _mainUow.TutorDepositEscrows.UpdateAsync(deposit);

            // KHÔNG gọi SaveChanges ở đây
            return true;
        }

        /// <summary>
        /// Helper: Refund tutor deposit
        /// </summary>
        private async Task<bool> RefundTutorDepositAsync(string adminUserId, TutorDepositEscrow deposit, string reason)
        {
            if (deposit.Status != TutorDepositStatus.Held)
                return false;

            var adminWallet = await _mainUow.Wallets.GetByUserIdAsync(_systemWalletOptions.SystemWalletUserId);
            if (adminWallet == null)
            {
                // Tạo wallet nếu chưa có
                adminWallet = new Wallet
                {
                    UserId = _systemWalletOptions.SystemWalletUserId,
                    Balance = 0,
                    Currency = "VND",
                    IsFrozen = false
                };
                await _mainUow.Wallets.AddAsync(adminWallet);
                await _mainUow.SaveChangesAsync();
            }

            // Lấy tutor wallet trực tiếp từ TutorUserId (không cần TutorProfile)
            var tutorWallet = await _mainUow.Wallets.GetByUserIdAsync(deposit.TutorUserId);
            if (tutorWallet == null)
            {
                // Tạo wallet nếu chưa có
                tutorWallet = new Wallet
                {
                    UserId = deposit.TutorUserId,
                    Balance = 0,
                    Currency = "VND",
                    IsFrozen = false
                };
                await _mainUow.Wallets.AddAsync(tutorWallet);
            }

            if (adminWallet.Balance < deposit.DepositAmount)
                return false;

            adminWallet.Balance -= deposit.DepositAmount;
            tutorWallet.Balance += deposit.DepositAmount;

            await _mainUow.Wallets.Update(adminWallet);
            await _mainUow.Wallets.Update(tutorWallet);

            await _mainUow.Transactions.AddAsync(new Transaction
            {
                WalletId = adminWallet.Id,
                Type = TransactionType.DepositRefundOut,
                Status = TransactionStatus.Succeeded,
                Amount = -deposit.DepositAmount,
                Note = $"Hoàn cọc cho tutor: {reason}",
                CounterpartyUserId = deposit.TutorUserId
            });

            await _mainUow.Transactions.AddAsync(new Transaction
            {
                WalletId = tutorWallet.Id,
                Type = TransactionType.DepositRefundIn,
                Status = TransactionStatus.Succeeded,
                Amount = deposit.DepositAmount,
                Note = $"Nhận hoàn cọc: {reason}",
                CounterpartyUserId = adminUserId
            });

            deposit.Status = TutorDepositStatus.Refunded;
            deposit.RefundedAt = DateTimeHelper.VietnamNow;
            await _mainUow.TutorDepositEscrows.UpdateAsync(deposit);

            await _mainUow.SaveChangesAsync();

            // Gửi notification cho tutor khi deposit được refund
            if (!string.IsNullOrEmpty(deposit.TutorUserId))
            {
                try
                {
                    var notification = await _notificationService.CreateWalletNotificationAsync(
                        deposit.TutorUserId,
                        NotificationType.TutorDepositRefunded,
                        deposit.DepositAmount,
                        $"Tiền cọc đã được hoàn lại: {reason}",
                        deposit.Id);
                    await _mainUow.SaveChangesAsync();
                    await _notificationService.SendRealTimeNotificationAsync(deposit.TutorUserId, notification);
                }
                catch (Exception notifEx)
                {
                    System.Diagnostics.Debug.WriteLine($"Failed to send notification: {notifEx.Message}");
                }
            }

            return true;
        }

        /// <summary>
        /// Helper: Lấy message lý do hủy lớp
        /// </summary>
        private string GetCancelReasonMessage(ClassCancelReason reason, string? note)
        {
            var baseMessage = reason switch
            {
                ClassCancelReason.SystemError => "Lớp học đã bị hủy do lỗi hệ thống/setup.",
                ClassCancelReason.TutorFault => "Lớp học đã bị hủy do lỗi từ phía gia sư.",
                ClassCancelReason.StudentFault => "Lớp học đã bị hủy do lỗi từ phía học sinh.",
                ClassCancelReason.MutualConsent => "Lớp học đã được hủy theo thỏa thuận giữa hai bên.",
                ClassCancelReason.PolicyViolation => "Lớp học đã bị hủy do vi phạm chính sách.",
                ClassCancelReason.DuplicateClass => "Lớp học đã bị hủy do trùng lặp.",
                ClassCancelReason.IncorrectInfo => "Lớp học đã bị hủy do thông tin không chính xác.",
                ClassCancelReason.Other => "Lớp học đã bị hủy do lý do khác.",
                _ => "Lớp học đã bị hủy."
            };

            if (!string.IsNullOrWhiteSpace(note))
            {
                return $"{baseMessage} Lý do: {note}";
            }

            return baseMessage;
        }

        /// <summary>
        /// Tạo message thông báo hoàn tiền cho học sinh dựa trên reason
        /// </summary>
        private string GetStudentRefundMessage(
            ClassCancelReason reason, 
            decimal totalRefunded, 
            int refundedCount, 
            bool hasStartedTeaching, 
            decimal teachingPercentage, 
            string? note)
        {
            string baseMessage = reason switch
            {
                ClassCancelReason.SystemError 
                or ClassCancelReason.PolicyViolation 
                or ClassCancelReason.DuplicateClass 
                or ClassCancelReason.IncorrectInfo 
                or ClassCancelReason.Other
                    => $"Bạn sẽ nhận hoàn tiền 100% học phí ({totalRefunded:N0} VND) do lỗi hệ thống.",
                
                ClassCancelReason.TutorFault
                    => hasStartedTeaching
                        ? $"Bạn sẽ nhận hoàn tiền {(1 - teachingPercentage):P0} học phí ({totalRefunded:N0} VND) do lỗi từ phía gia sư. Gia sư đã dạy {teachingPercentage:P0} buổi học."
                        : $"Bạn sẽ nhận hoàn tiền 100% học phí ({totalRefunded:N0} VND) do gia sư chưa bắt đầu dạy.",
                
                ClassCancelReason.StudentFault
                    => "Bạn sẽ không nhận hoàn tiền do lỗi từ phía bạn.",
                
                ClassCancelReason.MutualConsent
                    => hasStartedTeaching
                        ? $"Bạn sẽ nhận hoàn tiền 80% phần còn lại ({totalRefunded:N0} VND) theo thỏa thuận. Gia sư đã dạy {teachingPercentage:P0} buổi học."
                        : $"Bạn sẽ nhận hoàn tiền 80% học phí ({totalRefunded:N0} VND) theo thỏa thuận.",
                
                _ => refundedCount > 0 
                    ? $"Bạn sẽ nhận hoàn tiền {totalRefunded:N0} VND."
                    : "Không có hoàn tiền."
            };

            if (!string.IsNullOrWhiteSpace(note))
            {
                return $"{baseMessage} Ghi chú: {note}";
            }

            return baseMessage;
        }

        /// <summary>
        /// Tạo message thông báo hoàn tiền cọc cho gia sư dựa trên reason
        /// </summary>
        private string GetTutorDepositMessage(
            ClassCancelReason reason, 
            bool depositRefunded, 
            decimal? depositRefundAmount, 
            bool hasStartedTeaching, 
            decimal teachingPercentage, 
            string? note)
        {
            string baseMessage = reason switch
            {
                ClassCancelReason.SystemError 
                or ClassCancelReason.PolicyViolation 
                or ClassCancelReason.DuplicateClass 
                or ClassCancelReason.IncorrectInfo 
                or ClassCancelReason.Other
                    => depositRefunded && depositRefundAmount.HasValue
                        ? $"Bạn sẽ nhận hoàn tiền cọc 100% ({depositRefundAmount.Value:N0} VND) do lỗi hệ thống."
                        : "Không có tiền cọc để hoàn.",
                
                ClassCancelReason.TutorFault
                    => depositRefundAmount.HasValue
                        ? $"Tiền cọc {depositRefundAmount.Value:N0} VND đã bị tịch thu do lỗi của bạn{(hasStartedTeaching ? $" (đã dạy {teachingPercentage:P0} buổi)" : " (chưa bắt đầu dạy)")}."
                        : "Tiền cọc đã bị tịch thu do lỗi của bạn.",
                
                ClassCancelReason.StudentFault
                    => depositRefunded && depositRefundAmount.HasValue
                        ? $"Bạn sẽ nhận hoàn tiền cọc 100% ({depositRefundAmount.Value:N0} VND) do lỗi từ phía học sinh."
                        : "Không có tiền cọc để hoàn.",
                
                ClassCancelReason.MutualConsent
                    => depositRefunded && depositRefundAmount.HasValue
                        ? $"Bạn sẽ nhận hoàn tiền cọc 100% ({depositRefundAmount.Value:N0} VND) theo thỏa thuận."
                        : "Không có tiền cọc để hoàn.",
                
                _ => depositRefunded && depositRefundAmount.HasValue
                    ? $"Bạn sẽ nhận hoàn tiền cọc {depositRefundAmount.Value:N0} VND."
                    : "Không có tiền cọc để hoàn."
            };

            if (!string.IsNullOrWhiteSpace(note))
            {
                return $"{baseMessage} Ghi chú: {note}";
            }

            return baseMessage;
        }

        #endregion

        #region Helper

        /// <summary>
        /// Kiểm tra duplicate class - Phòng chống trùng lặp
        /// Kiểm tra xem có lớp học tương tự đã tồn tại với:
        /// - Cùng gia sư
        /// - Cùng môn học
        /// - Cùng cấp độ
        /// - Cùng mode (Online/Offline)
        /// - Lịch học trùng lặp (cùng ngày và thời gian chồng chéo)
        /// - Giá tương tự (trong khoảng ±10%)
        /// </summary>
        private async Task CheckForDuplicateClassAsync(string tutorId, CreateClassDto createDto)
        {
            await CheckForDuplicateClassAsync(
                tutorId,
                createDto.Subject,
                createDto.EducationLevel,
                createDto.Mode,
                createDto.Price,
                createDto.ScheduleRules.Select(r => new { r.DayOfWeek, r.StartTime, r.EndTime }).ToList()
            );
        }

        /// <summary>
        /// Overload method để kiểm tra duplicate với các tham số trực tiếp
        /// </summary>
        private async Task CheckForDuplicateClassAsync(
            string tutorId,
            string subject,
            string educationLevel,
            ClassMode mode,
            decimal price,
            IEnumerable<dynamic> scheduleRules)
        {
            // Tìm các lớp học của cùng gia sư với cùng môn học, cấp độ và mode
            var existingClasses = await _uow.Classes.GetAllAsync(
                filter: c => c.TutorId == tutorId
                           && c.Subject == subject
                           && c.EducationLevel == educationLevel
                           && c.Mode == mode
                           && c.DeletedAt == null
                           && (c.Status == ClassStatus.Pending || c.Status == ClassStatus.Ongoing),
                includes: q => q.Include(c => c.ClassSchedules)
            );

            if (!existingClasses.Any())
                return; // Không có lớp nào tương tự

            // Kiểm tra giá tương tự (trong khoảng ±10%)
            decimal priceTolerance = price * 0.1m;
            var similarPriceClasses = existingClasses
                .Where(c => c.Price.HasValue && Math.Abs(c.Price.Value - price) <= priceTolerance)
                .ToList();

            if (!similarPriceClasses.Any())
                return; // Không có lớp nào có giá tương tự

            // Kiểm tra lịch học trùng lặp
            foreach (var existingClass in similarPriceClasses)
            {
                if (existingClass.ClassSchedules == null || !existingClass.ClassSchedules.Any())
                    continue;

                // So sánh từng lịch học trong lớp mới với các lịch học trong lớp hiện có
                foreach (var newSchedule in scheduleRules)
                {
                    foreach (var existingSchedule in existingClass.ClassSchedules)
                    {
                        // Kiểm tra cùng ngày trong tuần
                        if ((DayOfWeek)existingSchedule.DayOfWeek == newSchedule.DayOfWeek)
                        {
                            // Kiểm tra thời gian chồng chéo
                            // Hai khoảng thời gian chồng chéo nếu: start1 < end2 && start2 < end1
                            TimeSpan newStart = newSchedule.StartTime;
                            TimeSpan newEnd = newSchedule.EndTime;
                            TimeSpan existingStart = existingSchedule.StartTime;
                            TimeSpan existingEnd = existingSchedule.EndTime;

                            if (newStart < existingEnd && existingStart < newEnd)
                            {
                                throw new InvalidOperationException(
                                    $"Đã tồn tại lớp học tương tự (ID: {existingClass.Id}, Tiêu đề: {existingClass.Title}) " +
                                    $"với cùng môn học, cấp độ, mode và lịch học trùng lặp vào {newSchedule.DayOfWeek} " +
                                    $"từ {newStart:hh\\:mm} đến {newEnd:hh\\:mm}. " +
                                    "Vui lòng kiểm tra lại hoặc hủy lớp học trùng lặp trước khi tạo lớp mới.");
                            }
                        }
                    }
                }
            }
        }

        private ClassDto MapToClassDto(Class cls, List<RecurringScheduleRuleDto> rules = null)
        {
            // cls must include ClassSchedules to map
            var scheduleRules = rules ?? cls.ClassSchedules?.Select(r => new RecurringScheduleRuleDto
            {
                DayOfWeek = (DayOfWeek)r.DayOfWeek,
                StartTime = r.StartTime,
                EndTime = r.EndTime
            }).ToList();

            return new ClassDto
            {
                Id = cls.Id,
                // Tutor identity fields
                TutorId = cls.TutorId,
                TutorUserId = cls.Tutor?.UserId,
                TutorName = cls.Tutor?.User?.UserName ?? cls.Tutor?.User?.UserName ?? "N/A",
                // other class fields
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
                Mode = cls.Mode.ToString(), // Enum -> String
                ClassStartDate = cls.ClassStartDate,
                OnlineStudyLink = cls.OnlineStudyLink,
                // return schedule rules
                ScheduleRules = scheduleRules
            };
        }

        /// <summary>
        /// Đồng bộ lại CurrentStudentCount cho một lớp dựa trên số ClassAssign thực tế có PaymentStatus = Paid
        /// Dùng để sửa các lớp đã bị lệch CurrentStudentCount do logic cũ
        /// </summary>
        public async Task<bool> SyncCurrentStudentCountAsync(string classId)
        {
            var classEntity = await _uow.Classes.GetByIdAsync(classId);
            if (classEntity == null)
                throw new KeyNotFoundException("Không tìm thấy lớp học.");

            // Tính lại CurrentStudentCount dựa trên số ClassAssign thực tế có PaymentStatus = Paid
            var actualPaidCount = await _context.ClassAssigns
                .CountAsync(ca => ca.ClassId == classId && ca.PaymentStatus == PaymentStatus.Paid);
            
            classEntity.CurrentStudentCount = actualPaidCount;
            await _uow.Classes.UpdateAsync(classEntity);
            await _uow.SaveChangesAsync();

            return true;
        }

        /// <summary>
        /// Đồng bộ lại trạng thái các buổi học đã điểm danh đủ nhưng chưa được chuyển sang COMPLETED
        /// Trả về số buổi học đã được sync
        /// </summary>
        public async Task<int> SyncLessonStatusForClassAsync(string classId)
        {
            var classEntity = await _uow.Classes.GetByIdAsync(classId);
            if (classEntity == null)
                throw new KeyNotFoundException("Không tìm thấy lớp học.");

            // Lấy danh sách học sinh trong lớp (có PaymentStatus = Paid)
            var studentsInClass = await _context.ClassAssigns
                .Where(ca => ca.ClassId == classId && ca.PaymentStatus == PaymentStatus.Paid)
                .Select(ca => ca.StudentId)
                .ToListAsync();

            if (!studentsInClass.Any())
                return 0; // Không có học sinh nào

            // Lấy tất cả buổi học của lớp chưa được complete
            var lessons = await _context.Lessons
                .Where(l => l.ClassId == classId && l.Status != LessonStatus.COMPLETED)
                .ToListAsync();

            int completedCount = 0;

            foreach (var lesson in lessons)
            {
                // Đếm số học sinh đã được điểm danh trong buổi học này
                var markedCount = await _context.Attendances
                    .Where(a => a.LessonId == lesson.Id && studentsInClass.Contains(a.StudentId))
                    .Select(a => a.StudentId)
                    .Distinct()
                    .CountAsync();

                // Nếu tất cả học sinh đã được điểm danh → chuyển Lesson.Status = COMPLETED
                if (markedCount == studentsInClass.Count)
                {
                    lesson.Status = LessonStatus.COMPLETED;
                    await _uow.Lessons.UpdateAsync(lesson);
                    completedCount++;
                }
            }

            if (completedCount > 0)
            {
                await _uow.SaveChangesAsync();
            }

            return completedCount;
        }

        #endregion
    }
}