using BusinessLayer.DTOs.Schedule.Class;
using BusinessLayer.DTOs.Schedule.ClassAssign;
using BusinessLayer.DTOs.Wallet;
using BusinessLayer.Helper;
using BusinessLayer.Options;
using BusinessLayer.Service.Interface;
using BusinessLayer.Service.Interface.IScheduleService;
using DataLayer.Entities;
using DataLayer.Enum;
using DataLayer.Repositories.Abstraction;
using DataLayer.Repositories.Abstraction.Schedule;
using Humanizer;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace BusinessLayer.Service.ScheduleService
{
    public class AssignService : IAssignService
    {
        private readonly TpeduContext _context;
        private readonly IScheduleUnitOfWork _uow;
        private readonly IUnitOfWork _mainUow;
        private readonly IStudentProfileService _studentProfileService;
        private readonly ITutorProfileService _tutorProfileService;
        private readonly IScheduleGenerationService _scheduleGenerationService;
        private readonly IEscrowService _escrowService;
        private readonly INotificationService _notificationService;
        private readonly IConversationService _conversationService;
        private readonly IParentProfileRepository _parentRepo;
        private readonly IWalletService _walletService;
        private readonly SystemWalletOptions _systemWalletOptions;


        public AssignService(
            TpeduContext context,
            IScheduleUnitOfWork uow,
            IUnitOfWork mainUow,
            IStudentProfileService studentProfileService,
            ITutorProfileService tutorProfileService,
            IScheduleGenerationService scheduleGenerationService,
            IEscrowService escrowService,
            INotificationService notificationService,
            IConversationService conversationService,
            IParentProfileRepository parentRepo,
            IWalletService walletService,
            IOptions<SystemWalletOptions> systemWalletOptions)
        {
            _context = context;
            _uow = uow;
            _mainUow = mainUow;
            _studentProfileService = studentProfileService;
            _tutorProfileService = tutorProfileService;
            _scheduleGenerationService = scheduleGenerationService;
            _escrowService = escrowService;
            _notificationService = notificationService;
            _conversationService = conversationService;
            _parentRepo = parentRepo;
            _walletService = walletService;
            _systemWalletOptions = systemWalletOptions.Value;
        }

        #region Student actions

        /// <summary>
        /// Assign student to recurring class
        /// Transaction: Check -> Subtract wallet -> Assign -> Update Status (if full) -> Commit
        /// </summary>
        public async Task<ClassAssignDetailDto> AssignRecurringClassAsync(string actorUserId, string userRole, AssignRecurringClassDto dto)
        {
            // Validate user and resolve target student
            string payerUserId = actorUserId; // The user performing the action and the payer
            string targetStudentId = await ResolveTargetStudentProfileIdAsync(actorUserId, userRole, dto.StudentId);

            // Validate class existence and status
            var classEntity = await _uow.Classes.GetByIdAsync(dto.ClassId);
            if (classEntity == null)
                throw new KeyNotFoundException("Lớp học không tồn tại.");

            if (classEntity.Status != ClassStatus.Pending)
                throw new InvalidOperationException($"Không thể đăng ký lớp đang ở trạng thái '{classEntity.Status}'.");

            if (classEntity.CurrentStudentCount >= classEntity.StudentLimit)
                throw new InvalidOperationException("Lớp học đã đủ số lượng học viên.");

            // Check enrollment (bao gồm cả những ClassAssign đã bị soft delete hoặc có PaymentStatus = Refunded)
            var existingAssign = await _uow.ClassAssigns.GetAsync(ca => ca.ClassId == dto.ClassId && ca.StudentId == targetStudentId);
            
            ClassAssign newAssign;
            bool isReusingExisting = false; // Flag để biết có đang reuse ClassAssign cũ không
            if (existingAssign != null)
            {
                // Cho phép đăng ký lại nếu:
                // 1. PaymentStatus = Refunded (đã rút và được hoàn tiền)
                // 2. PaymentStatus = Pending (đã rút nhưng chưa thanh toán)
                // KHÔNG cho phép nếu PaymentStatus = Paid (đã thanh toán và chưa rút)
                if (existingAssign.PaymentStatus == PaymentStatus.Refunded || 
                    existingAssign.PaymentStatus == PaymentStatus.Pending)
                {
                    // Reset ClassAssign để đăng ký lại
                    existingAssign.PaymentStatus = PaymentStatus.Pending;
                    existingAssign.ApprovalStatus = ApprovalStatus.Approved;
                    existingAssign.EnrolledAt = DateTimeHelper.VietnamNow;
                    existingAssign.DeletedAt = null; // Reset DeletedAt để cho phép đăng ký lại
                    existingAssign.UpdatedAt = DateTimeHelper.VietnamNow;
                    await _uow.ClassAssigns.UpdateAsync(existingAssign);
                    await _uow.SaveChangesAsync();
                    newAssign = existingAssign;
                    isReusingExisting = true;
                }
                else if (existingAssign.PaymentStatus == PaymentStatus.Paid)
                {
                    // Đã thanh toán và chưa rút → không cho đăng ký lại
                    throw new InvalidOperationException("Học sinh này đã đăng ký và thanh toán lớp học này rồi.");
                }
                else
                {
                    // Trường hợp khác (Failed, Expired) → cho phép đăng ký lại
                    existingAssign.PaymentStatus = PaymentStatus.Pending;
                    existingAssign.ApprovalStatus = ApprovalStatus.Approved;
                    existingAssign.EnrolledAt = DateTimeHelper.VietnamNow;
                    existingAssign.DeletedAt = null; // Reset DeletedAt để cho phép đăng ký lại
                    existingAssign.UpdatedAt = DateTimeHelper.VietnamNow;
                    await _uow.ClassAssigns.UpdateAsync(existingAssign);
                    await _uow.SaveChangesAsync();
                    newAssign = existingAssign;
                    isReusingExisting = true;
                }
            }
            else
            {
                // Tạo ClassAssign mới (với PaymentStatus = Pending)
                newAssign = new ClassAssign
                {
                    Id = Guid.NewGuid().ToString(),
                    ClassId = dto.ClassId,
                    StudentId = targetStudentId,
                    ApprovalStatus = ApprovalStatus.Approved,
                    PaymentStatus = PaymentStatus.Pending, // Sẽ được cập nhật thành Paid sau khi PayEscrowAsync thành công
                    EnrolledAt = DateTimeHelper.VietnamNow
                };
                await _uow.ClassAssigns.CreateAsync(newAssign);
                await _uow.SaveChangesAsync(); // Save để có newAssign.Id
            }

            // Lấy UserId của học sinh từ StudentProfileId để truyền vào PayEscrowRequest
            var studentProfile = await _mainUow.StudentProfiles.GetByIdAsync(targetStudentId);
            if (studentProfile == null || string.IsNullOrEmpty(studentProfile.UserId))
                throw new KeyNotFoundException("Không tìm thấy thông tin học sinh.");

            var studentUserId = studentProfile.UserId;

            // Offline class: thu phí kết nối 50,000 VND, đánh dấu Paid ngay
            if (classEntity.Mode == ClassMode.Offline)
            {
                const decimal CONNECTION_FEE = 50000m; // Phí kết nối: 50,000 VND
                
                // Đảm bảo wallets đã tồn tại
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

                // Kiểm tra số dư ví học sinh
                if (studentWallet.IsFrozen)
                    throw new InvalidOperationException("Ví của bạn đã bị khóa. Vui lòng liên hệ admin.");

                if (studentWallet.Balance < CONNECTION_FEE)
                    throw new InvalidOperationException($"Số dư không đủ. Cần {CONNECTION_FEE:N0} VND (phí kết nối), hiện có {studentWallet.Balance:N0} VND.");

                // Trừ phí kết nối từ ví học sinh và cộng vào ví admin
                studentWallet.Balance -= CONNECTION_FEE;
                adminWallet.Balance += CONNECTION_FEE;

                await _mainUow.Wallets.Update(studentWallet);
                await _mainUow.Wallets.Update(adminWallet);

                // Tạo transaction records
                await _mainUow.Transactions.AddAsync(new Transaction
                {
                    WalletId = studentWallet.Id,
                    Type = TransactionType.PayEscrow, // Dùng PayEscrow type cho phí kết nối
                    Status = TransactionStatus.Succeeded,
                    Amount = -CONNECTION_FEE,
                    Note = $"Phí kết nối lớp học offline {dto.ClassId}",
                    CounterpartyUserId = _systemWalletOptions.SystemWalletUserId
                });

                await _mainUow.Transactions.AddAsync(new Transaction
                {
                    WalletId = adminWallet.Id,
                    Type = TransactionType.EscrowIn, // Dùng EscrowIn type
                    Status = TransactionStatus.Succeeded,
                    Amount = CONNECTION_FEE,
                    Note = $"Nhận phí kết nối lớp học offline {dto.ClassId} từ học sinh",
                    CounterpartyUserId = studentUserId
                });

                newAssign.PaymentStatus = PaymentStatus.Paid;
                await _uow.ClassAssigns.UpdateAsync(newAssign);
                await _mainUow.SaveChangesAsync();
                await _uow.SaveChangesAsync();
            }
            else
            {
                // Online class: thực hiện thanh toán escrow
                var payEscrowResult = await _escrowService.PayEscrowAsync(payerUserId, new PayEscrowRequest
                {
                    ClassId = dto.ClassId,
                    PayerStudentUserId = studentUserId, // UserId của học sinh (để PayEscrowAsync tìm đúng StudentProfileId)
                    ClassAssignId = newAssign.Id // Truyền ClassAssignId để dùng trực tiếp entity đã được track
                });

                if (payEscrowResult.Status != "Ok")
                {
                    // Nếu PayEscrowAsync thất bại
                    if (isReusingExisting)
                    {
                        // Nếu là reuse ClassAssign cũ → reset về Refunded (không xóa vì có foreign key với Escrow)
                        newAssign.PaymentStatus = PaymentStatus.Refunded;
                        await _uow.ClassAssigns.UpdateAsync(newAssign);
                        await _uow.SaveChangesAsync();
                    }
                    else
                    {
                        // Nếu là ClassAssign mới → xóa
                        _context.ClassAssigns.Remove(newAssign);
                        await _uow.SaveChangesAsync();
                    }
                    throw new InvalidOperationException($"Thanh toán escrow thất bại: {payEscrowResult.Message}");
                }
            }

            // Logic cập nhật Class status
            var executionStrategy = _context.Database.CreateExecutionStrategy();
            return await executionStrategy.ExecuteAsync(async () =>
            {
                DateTime? scheduleEndDate = null;

                using var transaction = await _context.Database.BeginTransactionAsync();
                try
                {
                    // Reload class entity để có dữ liệu mới nhất
                    classEntity = await _uow.Classes.GetByIdAsync(dto.ClassId);
                    if (classEntity == null)
                        throw new KeyNotFoundException("Lớp học không tồn tại.");

                    // Tính lại CurrentStudentCount dựa trên số ClassAssign thực tế có PaymentStatus = Paid
                    // (offline được set Paid ngay, online set Paid sau escrow)
                    var actualPaidCount = await _context.ClassAssigns
                        .CountAsync(ca => ca.ClassId == dto.ClassId && ca.PaymentStatus == PaymentStatus.Paid);
                    var oldCount = classEntity.CurrentStudentCount;
                    classEntity.CurrentStudentCount = actualPaidCount;
                    Console.WriteLine($"[AssignRecurringClassAsync] Cập nhật CurrentStudentCount cho lớp {dto.ClassId}: {oldCount} → {actualPaidCount} (sau khi đăng ký, chưa thanh toán nên có thể chưa tăng)");

                    // Logic chuyển sang Ongoing:
                    // - Lớp ONLINE: Cần học sinh thanh toán + gia sư đặt cọc (KHÔNG CẦN đợi ngày bắt đầu)
                    // - Lớp OFFLINE: Cần đến ngày bắt đầu + có học sinh thanh toán
                    if (classEntity.Mode == ClassMode.Online)
                    {
                        // Lớp ONLINE: Kiểm tra học sinh thanh toán + gia sư đặt cọc
                        var hasPaidStudents = classEntity.CurrentStudentCount > 0;
                        var tutorDeposit = await _mainUow.TutorDepositEscrows.GetByClassIdAsync(classEntity.Id);
                        var hasTutorDeposit = tutorDeposit != null && tutorDeposit.Status == TutorDepositStatus.Held;
                        
                        Console.WriteLine($"[AssignRecurringClassAsync] Kiểm tra điều kiện chuyển sang Ongoing cho lớp ONLINE {dto.ClassId}:");
                        Console.WriteLine($"  - HasPaidStudents: {hasPaidStudents} (CurrentStudentCount: {classEntity.CurrentStudentCount})");
                        Console.WriteLine($"  - HasTutorDeposit: {hasTutorDeposit} (Deposit Status: {tutorDeposit?.Status})");
                        
                        if (hasPaidStudents && hasTutorDeposit)
                        {
                            var oldStatus = classEntity.Status;
                            classEntity.Status = ClassStatus.Ongoing;
                            Console.WriteLine($"[AssignRecurringClassAsync] ✅ Lớp {dto.ClassId} chuyển từ {oldStatus} → Ongoing (học sinh đã thanh toán + gia sư đã đặt cọc)");
                        }
                        else
                        {
                            // Chưa đủ điều kiện → giữ nguyên Pending
                            if (classEntity.Status != ClassStatus.Pending)
                            {
                                classEntity.Status = ClassStatus.Pending;
                            }
                            Console.WriteLine($"[AssignRecurringClassAsync] ⏳ Lớp {dto.ClassId} vẫn ở Pending (chưa đủ điều kiện: hasPaidStudents={hasPaidStudents}, hasTutorDeposit={hasTutorDeposit})");
                        }
                    }
                    else
                    {
                        // Lớp OFFLINE: Chỉ cần có học sinh thanh toán → chuyển sang Ongoing ngay (KHÔNG CẦN đợi ngày bắt đầu)
                        var hasPaidStudents = classEntity.CurrentStudentCount > 0;
                        
                        Console.WriteLine($"[AssignRecurringClassAsync] Kiểm tra điều kiện chuyển sang Ongoing cho lớp OFFLINE {dto.ClassId}:");
                        Console.WriteLine($"  - HasPaidStudents: {hasPaidStudents} (CurrentStudentCount: {classEntity.CurrentStudentCount})");
                        
                        if (hasPaidStudents)
                        {
                            var oldStatus = classEntity.Status;
                            classEntity.Status = ClassStatus.Ongoing;
                            Console.WriteLine($"[AssignRecurringClassAsync] ✅ Lớp OFFLINE {dto.ClassId} chuyển từ {oldStatus} → Ongoing (học sinh đã thanh toán)");
                        }
                        else
                        {
                            // Chưa có học sinh thanh toán → giữ nguyên Pending
                            if (classEntity.Status != ClassStatus.Pending)
                            {
                                classEntity.Status = ClassStatus.Pending;
                            }
                            Console.WriteLine($"[AssignRecurringClassAsync] ⏳ Lớp OFFLINE {dto.ClassId} vẫn ở Pending (chưa có học sinh thanh toán)");
                        }
                    }

                    await _uow.Classes.UpdateAsync(classEntity);

                    // Sinh lịch ngay khi thanh toán thành công (không cần đợi đến ngày bắt đầu)
                    // Lịch sẽ được sinh dựa trên ClassStartDate hoặc ngày hiện tại
                    await GenerateLessonsOnPaymentAsync(classEntity);

                    // Commit Transaction
                    await _uow.SaveChangesAsync();
                    await _mainUow.SaveChangesAsync();
                    await transaction.CommitAsync();
                }
                catch
                {
                    // If any error, rollback transaction
                    await transaction.RollbackAsync();
                    throw;
                }

                try
                {
                    // Send Notifications

                    string successMessage = $"Thanh toán thành công {classEntity.Price:N0} VND cho lớp {classEntity.Title}.";

                    if (scheduleEndDate.HasValue)
                    {
                        var localEndDate = scheduleEndDate.Value.ToLocalTime();
                        successMessage += $" Lớp học dự kiến kết thúc vào ngày {localEndDate:dd/MM/yyyy}.";
                    }
                    // Notify payer about successful payment
                    await _notificationService.CreateAccountNotificationAsync(
                        payerUserId,
                        NotificationType.EscrowPaid,
                        $"Thanh toán thành công {classEntity.Price:N0} VND cho lớp {classEntity.Title}.",
                        classEntity.Id);

                    // If parent paid, notify the student as well
                    if (userRole == "Parent")
                    {
                        var studentUser = await _mainUow.StudentProfiles.GetByIdAsync(targetStudentId);
                        if (studentUser?.UserId != null)
                        {
                            await _notificationService.CreateAccountNotificationAsync(
                                studentUser.UserId,
                                NotificationType.ClassEnrollmentSuccess,
                                $"Phụ huynh đã đăng ký thành công lớp {classEntity.Title} cho bạn.",
                                classEntity.Id);
                            await _uow.SaveChangesAsync();
                        }
                    }

                    // Tạo conversation và gửi notification về match/tạo nhóm
                    var tutorProfile = await _mainUow.TutorProfiles.GetByIdAsync(classEntity.TutorId);
                    var tutorUserId = tutorProfile?.UserId;
                    var studentProfile = await _mainUow.StudentProfiles.GetByIdAsync(targetStudentId);
                    var studentUserId = studentProfile?.UserId;

                    if (!string.IsNullOrEmpty(tutorUserId) && !string.IsNullOrEmpty(studentUserId))
                    {
                        // Nếu là lớp 1-1 (StudentLimit = 1) → tạo conversation 1-1
                        if (classEntity.StudentLimit == 1)
                        {
                            try
                            {
                                await _conversationService.GetOrCreateOneToOneConversationAsync(tutorUserId, studentUserId);
                                
                                // Gửi notification cho tutor
                                var tutorNotification = await _notificationService.CreateAccountNotificationAsync(
                                    tutorUserId,
                                    NotificationType.ClassEnrollmentSuccess,
                                    $"Học sinh đã đăng ký vào lớp {classEntity.Title}. Bạn đã được match với học sinh.",
                                    classEntity.Id);
                                await _notificationService.SendRealTimeNotificationAsync(tutorUserId, tutorNotification);

                                // Gửi notification cho student
                                var studentNotif = await _notificationService.CreateAccountNotificationAsync(
                                    studentUserId,
                                    NotificationType.ClassEnrollmentSuccess,
                                    $"Bạn đã được match với gia sư cho lớp {classEntity.Title}. Hãy bắt đầu trò chuyện!",
                                    classEntity.Id);
                                await _notificationService.SendRealTimeNotificationAsync(studentUserId, studentNotif);
                            }
                            catch (Exception convEx)
                            {
                                Console.WriteLine($"Tạo conversation 1-1 thất bại: {convEx.Message}");
                            }
                        }
                        // Nếu là lớp group (StudentLimit > 1) → tạo hoặc thêm vào group conversation
                        else if (classEntity.StudentLimit > 1)
                        {
                            try
                            {
                                // Tạo hoặc lấy group conversation, tự động thêm student vào
                                await _conversationService.GetOrCreateClassConversationAsync(classEntity.Id, studentUserId);
                                
                                // Gửi notification cho tutor (chỉ khi học sinh đầu tiên vào)
                                if (classEntity.CurrentStudentCount == 1)
                                {
                                    var tutorNotification = await _notificationService.CreateAccountNotificationAsync(
                                        tutorUserId,
                                        NotificationType.ClassEnrollmentSuccess,
                                        $"Nhóm lớp {classEntity.Title} đã được tạo. Học sinh đầu tiên đã tham gia.",
                                        classEntity.Id);
                                    await _notificationService.SendRealTimeNotificationAsync(tutorUserId, tutorNotification);
                                }
                                else
                                {
                                    var tutorNotification = await _notificationService.CreateAccountNotificationAsync(
                                        tutorUserId,
                                        NotificationType.ClassEnrollmentSuccess,
                                        $"Có học sinh mới tham gia vào nhóm lớp {classEntity.Title}.",
                                        classEntity.Id);
                                    await _notificationService.SendRealTimeNotificationAsync(tutorUserId, tutorNotification);
                                }

                                // Gửi notification cho student
                                var studentNotif = await _notificationService.CreateAccountNotificationAsync(
                                    studentUserId,
                                    NotificationType.ClassEnrollmentSuccess,
                                    $"Bạn đã tham gia vào nhóm lớp {classEntity.Title}. Hãy bắt đầu trò chuyện với gia sư và các học sinh khác!",
                                    classEntity.Id);
                                await _notificationService.SendRealTimeNotificationAsync(studentUserId, studentNotif);
                            }
                            catch (Exception convEx)
                            {
                                Console.WriteLine($"Tạo group conversation thất bại: {convEx.Message}");
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    // Log error 
                    Console.WriteLine($"Gửi thông báo thất bại: {ex.Message}");
                }

                // Return Detail DTO
                return await GetEnrollmentDetailAsync(payerUserId, dto.ClassId);

            });
        }

        /// <summary>
        /// Sinh lịch ngay khi thanh toán thành công (không cần đợi đến ngày bắt đầu)
        /// </summary>
        private async Task GenerateLessonsOnPaymentAsync(Class classEntity)
        {
            // Only generate if no lessons exist yet
            var hasLessons = await _context.Lessons.AnyAsync(l => l.ClassId == classEntity.Id && l.DeletedAt == null);
            if (hasLessons)
            {
                Console.WriteLine($"[GenerateLessonsOnPaymentAsync] Lớp {classEntity.Id} đã có lessons, bỏ qua việc sinh lịch.");
                return;
            }

            Console.WriteLine($"[GenerateLessonsOnPaymentAsync] Bắt đầu sinh lịch cho lớp {classEntity.Id}...");

            if (classEntity.ClassSchedules == null || !classEntity.ClassSchedules.Any())
            {
                // Fallback: Nếu không load được schedule, thử load lại từ DB
                var schedules = await _context.ClassSchedules
                    .Where(cs => cs.ClassId == classEntity.Id)
                    .ToListAsync();
                classEntity.ClassSchedules = schedules;
                Console.WriteLine($"[GenerateLessonsOnPaymentAsync] Đã load {schedules.Count} ClassSchedules từ DB.");
            }

            if (classEntity.ClassSchedules != null && classEntity.ClassSchedules.Any())
            {
                try
                {
                    // Sinh lịch dựa trên ClassStartDate hoặc ngày hiện tại (nếu ClassStartDate là null)
                    var startDate = classEntity.ClassStartDate ?? DateTimeHelper.VietnamNow;
                    
                    Console.WriteLine($"[GenerateLessonsOnPaymentAsync] Sinh lịch cho lớp {classEntity.Id} với:");
                    Console.WriteLine($"  - TutorId: {classEntity.TutorId}");
                    Console.WriteLine($"  - StartDate: {startDate:dd/MM/yyyy HH:mm:ss}");
                    Console.WriteLine($"  - Số ClassSchedules: {classEntity.ClassSchedules.Count()}");
                    
                    // Gọi service sinh lịch
                    // Lưu ý: Hàm này dùng _context.AddRangeAsync nhưng chưa SaveChanges
                    // Việc SaveChanges sẽ được thực hiện ở caller
                    await _scheduleGenerationService.GenerateScheduleFromClassAsync(
                        classEntity.Id,
                        classEntity.TutorId,
                        startDate,
                        classEntity.ClassSchedules
                    );
                    
                    // Kiểm tra lại xem đã sinh được lessons chưa
                    var lessonsCount = await _context.Lessons.CountAsync(l => l.ClassId == classEntity.Id && l.DeletedAt == null);
                    var scheduleEntriesCount = await _context.ScheduleEntries.CountAsync(se => 
                        se.Lesson != null && 
                        se.Lesson.ClassId == classEntity.Id && 
                        se.DeletedAt == null);
                    
                    if (lessonsCount == 0 || scheduleEntriesCount == 0)
                    {
                        Console.WriteLine($"[GenerateLessonsOnPaymentAsync] CẢNH BÁO: Không sinh được lịch cho lớp {classEntity.Id}!");
                        Console.WriteLine($"  - Số lessons: {lessonsCount}");
                        Console.WriteLine($"  - Số schedule entries: {scheduleEntriesCount}");
                        Console.WriteLine($"  - Có thể do gia sư chưa có lịch rảnh hoặc lịch rảnh bị conflict với ClassSchedules.");
                    }
                    else
                    {
                        Console.WriteLine($"[GenerateLessonsOnPaymentAsync] Đã sinh lịch cho lớp {classEntity.Id}:");
                        Console.WriteLine($"  - Số lessons: {lessonsCount}");
                        Console.WriteLine($"  - Số schedule entries: {scheduleEntriesCount}");
                    }
                }
                catch (Exception ex)
                {
                    // Log lỗi chi tiết nhưng không throw để không rollback transaction
                    // Lịch sẽ được sinh sau khi có slot trống
                    Console.WriteLine($"[GenerateLessonsOnPaymentAsync] LỖI khi sinh lịch cho lớp {classEntity.Id}:");
                    Console.WriteLine($"  - Message: {ex.Message}");
                    Console.WriteLine($"  - InnerException: {ex.InnerException?.Message}");
                    Console.WriteLine($"  - Stack trace: {ex.StackTrace}");
                    // Không throw exception để không rollback transaction cập nhật Class status
                }
            }
            else
            {
                Console.WriteLine($"[GenerateLessonsOnPaymentAsync] Lớp {classEntity.Id} không có ClassSchedules để sinh lịch.");
            }
        }

        /// <summary>
        /// Sinh lesson và schedule cho class nếu:
        /// - Class đang ở trạng thái Ongoing
        /// - Chưa có lesson nào trong DB
        /// </summary>
        private async Task GenerateLessonsIfNeededAsync(Class classEntity)
        {
            if (classEntity.Status != ClassStatus.Ongoing)
            {
                return;
            }

            // Only generate if no lessons exist yet
            var hasLessons = await _context.Lessons.AnyAsync(l => l.ClassId == classEntity.Id);
            if (hasLessons)
            {
                return;
            }

            if (classEntity.ClassSchedules == null || !classEntity.ClassSchedules.Any())
            {
                // Fallback: Nếu không load được schedule, thử load lại từ DB
                var schedules = await _context.ClassSchedules
                    .Where(cs => cs.ClassId == classEntity.Id)
                    .ToListAsync();
                classEntity.ClassSchedules = schedules;
            }

            if (classEntity.ClassSchedules != null && classEntity.ClassSchedules.Any())
            {
                try
                {
                    // Gọi service sinh lịch
                    // Lưu ý: Hàm này dùng _context.AddRangeAsync nhưng chưa SaveChanges
                    // Việc SaveChanges sẽ được thực hiện ở caller
                    await _scheduleGenerationService.GenerateScheduleFromClassAsync(
                        classEntity.Id,
                        classEntity.TutorId,
                        classEntity.ClassStartDate ?? DateTimeHelper.VietnamNow,
                        classEntity.ClassSchedules
                    );
                }
                catch (Exception ex)
                {
                    // Log lỗi nhưng không throw để không rollback transaction
                    // Lịch sẽ được sinh sau khi có slot trống
                    Console.WriteLine($"[GenerateLessonsIfNeededAsync] Lỗi khi sinh lịch cho lớp {classEntity.Id}: {ex.Message}");
                    Console.WriteLine($"[GenerateLessonsIfNeededAsync] Stack trace: {ex.StackTrace}");
                    // Không throw exception để không rollback transaction cập nhật Class status
                }
            }
            else
            {
                Console.WriteLine($"[GenerateLessonsIfNeededAsync] Lớp {classEntity.Id} không có ClassSchedules để sinh lịch.");
            }
        }

        /// <summary>
        /// [TRANSACTION] Student withdraw from class - Học sinh hủy enrollment
        /// Xử lý refund escrow cho học sinh khi rút khỏi lớp
        /// </summary>
        public async Task<bool> WithdrawFromClassAsync(string actorUserId, string userRole, string classId, string? studentId)
        {
            string targetStudentId = await ResolveTargetStudentProfileIdAsync(actorUserId, userRole, studentId);

            // Đảm bảo wallets đã tồn tại trước khi vào transaction
            // Lấy escrows để biết cần wallet nào
            var assignment = await _uow.ClassAssigns.GetAsync(
                a => a.StudentId == targetStudentId && a.ClassId == classId);

            if (assignment == null)
                throw new KeyNotFoundException("Bạn chưa ghi danh vào lớp học này.");

            var escrows = await _mainUow.Escrows.GetAllAsync(
                filter: e => e.ClassAssignId == assignment.Id && 
                           (e.Status == EscrowStatus.Held || e.Status == EscrowStatus.PartiallyReleased));

            // Chỉ đảm bảo wallets nếu có escrow cần refund
            if (escrows.Any())
            {
                // Đảm bảo admin wallet và student wallets đã tồn tại
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

                // Đảm bảo tất cả student wallets đã tồn tại
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
                await _mainUow.SaveChangesAsync();
            }

            // Sử dụng transaction từ _mainUow vì các helper methods cũng dùng _mainUow
            using var tx = await _mainUow.BeginTransactionAsync();
            try
            {
                // Reload assignment và class trong transaction
                assignment = await _uow.ClassAssigns.GetAsync(
                    a => a.StudentId == targetStudentId && a.ClassId == classId);

                if (assignment == null)
                    throw new KeyNotFoundException("Bạn chưa ghi danh vào lớp học này.");

                // load Class
                var targetClass = await _uow.Classes.GetByIdAsync(classId);
                if (targetClass == null)
                    throw new KeyNotFoundException("Không tìm thấy lớp học.");

                // validate Class status
                if (targetClass.Status == ClassStatus.Completed ||
                    targetClass.Status == ClassStatus.Cancelled)
                {
                    throw new InvalidOperationException($"Không thể rút khỏi lớp học đang ở trạng thái '{targetClass.Status}'.");
                }

                // Kiểm tra lớp đã bắt đầu chưa
                // Chỉ check Status = Ongoing, không check ClassStartDate vì có thể có time component
                bool hasClassStarted = targetClass.Status == ClassStatus.Ongoing;
                
                // Nếu lớp đã bắt đầu (Status = Ongoing) → KHÔNG cho phép rút
                if (hasClassStarted)
                {
                    throw new InvalidOperationException("Không thể rút khỏi lớp học đã bắt đầu.");
                }

                // Kiểm tra thời gian rút: Chỉ cho phép rút trước 1 ngày so với ngày bắt đầu
                if (targetClass.ClassStartDate.HasValue)
                {
                    var now = DateTimeHelper.VietnamNow;
                    var classStartDate = targetClass.ClassStartDate.Value.Date; // Lấy ngày (bỏ giờ)
                    var oneDayBefore = classStartDate.AddDays(-1); // Trước 1 ngày
                    var today = now.Date; // Ngày hôm nay

                    // Nếu hôm nay đã >= ngày bắt đầu - 1 ngày → không cho rút
                    if (today >= oneDayBefore)
                    {
                        throw new InvalidOperationException($"Chỉ có thể rút đăng ký trước 1 ngày so với ngày bắt đầu lớp học ({classStartDate:dd/MM/yyyy}). Hôm nay là {today:dd/MM/yyyy}.");
                    }
                }

                // Reload escrows trong transaction
                escrows = await _mainUow.Escrows.GetAllAsync(
                    filter: e => e.ClassAssignId == assignment.Id && 
                               (e.Status == EscrowStatus.Held || e.Status == EscrowStatus.PartiallyReleased));

                // Lưu danh sách escrows đã refund để gửi notification sau
                var refundedEscrows = new List<Escrow>();

                // CHỈ REFUND NẾU LỚP CHƯA BẮT ĐẦU
                if (!hasClassStarted && escrows.Any())
                {
                    foreach (var esc in escrows)
                    {
                        if (esc.Status == EscrowStatus.Held)
                        {
                            // Lớp chưa bắt đầu → Refund full
                            var refundSuccess = await RefundEscrowInTransactionAsync(actorUserId, esc);
                            if (!refundSuccess)
                            {
                                throw new InvalidOperationException($"Không thể hoàn tiền escrow {esc.Id}");
                            }
                            // Reload escrow để lưu vào list
                            var escForNotification = await _mainUow.Escrows.GetByIdAsync(esc.Id);
                            if (escForNotification != null)
                            {
                                refundedEscrows.Add(escForNotification);
                            }
                        }
                        else if (esc.Status == EscrowStatus.PartiallyReleased)
                        {
                            // Đã release một phần cho tutor → Refund phần còn lại
                            decimal remainingPercentage = 1.0m - (esc.ReleasedAmount / esc.GrossAmount);
                            if (remainingPercentage > 0)
                            {
                                var partialRefundSuccess = await PartialRefundEscrowInTransactionAsync(actorUserId, esc, remainingPercentage);
                                if (!partialRefundSuccess)
                                {
                                    throw new InvalidOperationException($"Không thể hoàn tiền escrow {esc.Id}");
                                }
                                // Reload escrow để lưu vào list
                                var escForNotification = await _mainUow.Escrows.GetByIdAsync(esc.Id);
                                if (escForNotification != null)
                                {
                                    refundedEscrows.Add(escForNotification);
                                }
                            }
                        }
                    }

                    // update PaymentStatus for ClassAssign nếu đã thanh toán và đã refund
                    if (assignment.PaymentStatus == PaymentStatus.Paid && refundedEscrows.Any())
                    {
                        assignment.PaymentStatus = PaymentStatus.Refunded;
                    }
                }
                else if (hasClassStarted)
                {
                    // Lớp đã bắt đầu → KHÔNG REFUND
                    // Học sinh vẫn có thể rút nhưng không được hoàn tiền
                    // Tuy nhiên, vẫn phải update PaymentStatus = Refunded để lịch học biến mất
                    // (Dù không có refund, nhưng học sinh đã rút nên không còn tham gia lớp nữa)
                }

                // Update ClassAssign status - KHÔNG XÓA vì Escrow có foreign key restrict đến ClassAssign
                // ClassAssign sẽ được giữ lại để audit, chỉ update status
                // Khi học sinh rút, luôn set PaymentStatus = Refunded để lịch học biến mất
                // (Dù có refund hay không, học sinh đã rút nên lịch học phải biến mất)
                if (assignment.PaymentStatus == PaymentStatus.Paid)
                {
                    assignment.PaymentStatus = PaymentStatus.Refunded;
                }
                await _uow.ClassAssigns.UpdateAsync(assignment);

                // Xóa Attendance records của học sinh này (vì học sinh đã rút)
                var studentAttendances = await _mainUow.Attendances.GetAllAsync(
                    filter: a => a.StudentId == targetStudentId && 
                               a.Lesson != null && 
                               a.Lesson.ClassId == classId &&
                               a.DeletedAt == null
                );
                
                if (studentAttendances.Any())
                {
                    var now = DateTimeHelper.VietnamNow;
                    foreach (var attendance in studentAttendances)
                    {
                        attendance.DeletedAt = now;
                        attendance.UpdatedAt = now;
                    }
                    Console.WriteLine($"[WithdrawFromClassAsync] Đã xóa {studentAttendances.Count()} attendance records của học sinh {targetStudentId}");
                }

                // Save changes để PaymentStatus = Refunded được commit vào database
                // TRƯỚC KHI đếm lại CurrentStudentCount
                await _uow.SaveChangesAsync();
                await _mainUow.SaveChangesAsync();

                // Tính lại CurrentStudentCount dựa trên số ClassAssign thực tế có PaymentStatus = Paid
                // Đảm bảo tính chính xác, tránh lỗi khi có học sinh rút hoặc có vấn đề với transaction
                // LƯU Ý: Phải SaveChanges TRƯỚC để PaymentStatus = Refunded được commit, nếu không CountAsync sẽ vẫn đếm được assignment cũ
                // Sử dụng _uow.ClassAssigns.GetByClassIdAsync thay vì _context.ClassAssigns.CountAsync để đảm bảo query từ cùng DbContext với assignment đã được update
                var allClassAssigns = await _uow.ClassAssigns.GetByClassIdAsync(classId, includeStudent: false);
                var actualPaidCount = allClassAssigns.Count(ca => ca.PaymentStatus == PaymentStatus.Paid && ca.DeletedAt == null);
                var oldCount = targetClass.CurrentStudentCount;
                targetClass.CurrentStudentCount = actualPaidCount;
                Console.WriteLine($"[WithdrawFromClassAsync] Cập nhật CurrentStudentCount cho lớp {classId}: {oldCount} → {actualPaidCount}");
                
                // KHÔNG xóa lessons và schedule entries khi học sinh rút
                // Vì:
                // 1. Lịch dạy của gia sư vẫn cần giữ nguyên (có thể còn học sinh khác)
                // 2. Lịch học của học sinh sẽ tự động biến mất vì ClassAssign.PaymentStatus = Refunded
                // 3. Chỉ xóa lessons/schedule khi hủy lớp (không phải khi học sinh rút)
                
                // Chỉ xóa lessons và schedule entries khi HỦY LỚP, không phải khi học sinh rút
                // Logic xóa lessons/schedule khi hủy lớp đã được xử lý trong CancelClassByTutorAsync và CancelClassByAdminAsync
                
                // KHÔNG set Status = Cancelled khi học sinh rút
                // Lớp vẫn ở trạng thái Pending để các học sinh khác có thể đăng ký
                // Chỉ tutor hoặc admin mới có thể hủy lớp
                await _uow.Classes.UpdateAsync(targetClass); // non save

                // save all
                await _uow.SaveChangesAsync();
                await _mainUow.SaveChangesAsync();
                await tx.CommitAsync();

                // Gửi notification sau khi commit transaction
                foreach (var esc in refundedEscrows)
                {
                    try
                    {
                        decimal refundAmount = esc.Status == EscrowStatus.Refunded 
                            ? esc.GrossAmount 
                            : (esc.GrossAmount - esc.ReleasedAmount - esc.RefundedAmount);
                        
                        var notification = await _notificationService.CreateEscrowNotificationAsync(
                            esc.StudentUserId,
                            NotificationType.EscrowRefunded,
                            refundAmount,
                            esc.ClassId,
                            esc.Id);
                        await _mainUow.SaveChangesAsync();
                        await _notificationService.SendRealTimeNotificationAsync(esc.StudentUserId, notification);
                    }
                    catch (Exception notifEx)
                    {
                        // Log lỗi nhưng không throw để không ảnh hưởng đến flow chính
                        Console.WriteLine($"WithdrawFromClassAsync: Lỗi khi gửi notification hoàn tiền cho student {esc.StudentUserId}: {notifEx.Message}");
                    }
                }
            }
            catch (Exception)
            {
                await tx.RollbackAsync();
                throw;
            }

            // Xóa participant khỏi conversation sau khi rút khỏi lớp
            try
            {
                var studentProfile = await _mainUow.StudentProfiles.GetByIdAsync(targetStudentId);
                if (studentProfile != null && !string.IsNullOrEmpty(studentProfile.UserId))
                {
                    // Lấy class để kiểm tra xem là lớp 1-1 hay group
                    var classEntity = await _uow.Classes.GetByIdAsync(classId);
                    if (classEntity != null)
                    {
                        // Nếu là lớp group, chỉ xóa participant
                        if (classEntity.StudentLimit > 1)
                        {
                            await _conversationService.RemoveParticipantFromClassConversationAsync(classId, studentProfile.UserId);
                        }
                        // Nếu là lớp 1-1, xóa toàn bộ conversation
                        else if (classEntity.StudentLimit == 1)
                        {
                            await _conversationService.DeleteClassConversationAsync(classId);
                        }
                    }
                }
            }
            catch (Exception convEx)
            {
                Console.WriteLine($"WithdrawFromClassAsync: Lỗi khi xóa conversation/participant: {convEx.Message}");
            }

            return true;
        }

        public async Task<List<MyEnrolledClassesDto>> GetMyEnrolledClassesAsync(string actorUserId, string userRole, string? studentId)
        {
            string targetStudentId = await ResolveTargetStudentProfileIdAsync(actorUserId, userRole, studentId);

            var allClassAssigns = await _uow.ClassAssigns.GetByStudentIdAsync(targetStudentId, includeClass: true);

            // Chỉ lấy lớp học chưa bị rút: PaymentStatus != Refunded và DeletedAt == null
            // Lọc để chỉ hiển thị lớp học đang active (chưa rút đăng ký)
            var activeClassAssigns = allClassAssigns
                .Where(ca => ca.PaymentStatus != PaymentStatus.Refunded 
                          && ca.DeletedAt == null
                          && ca.Class != null) // Đảm bảo Class không bị null
                .ToList();

            return activeClassAssigns.Select(ca => new MyEnrolledClassesDto
            {
                ClassId = ca.ClassId ?? string.Empty,
                ClassTitle = ca.Class?.Title ?? "N/A",
                Subject = ca.Class?.Subject,
                EducationLevel = ca.Class?.EducationLevel,
                TutorName = ca.Class?.Tutor?.User?.UserName ?? "N/A",
                Price = ca.Class?.Price ?? 0,
                ClassStatus = ca.Class?.Status ?? ClassStatus.Pending,
                ApprovalStatus = ca.ApprovalStatus,
                PaymentStatus = ca.PaymentStatus,
                EnrolledAt = ca.EnrolledAt,
                Location = ca.Class?.Location,
                Mode = ca.Class?.Mode ?? ClassMode.Offline,
                ClassStartDate = ca.Class?.ClassStartDate
            }).ToList();
        }

        public async Task<EnrollmentCheckDto> CheckEnrollmentAsync(string actorUserId, string userRole, string classId, string? studentId)
        {
            string targetStudentId = await ResolveTargetStudentProfileIdAsync(actorUserId, userRole, studentId);

            var isEnrolled = await _uow.ClassAssigns.IsApprovedAsync(classId, targetStudentId);

            return new EnrollmentCheckDto
            {
                ClassId = classId,
                IsEnrolled = isEnrolled
            };
        }

        public async Task<ClassAssignDetailDto> GetEnrollmentDetailAsync(string userId, string classId)
        {
            // Student/Parent can only view their own enrollment
            var assigns = await _uow.ClassAssigns.GetAllAsync(
                filter: ca => ca.ClassId == classId,
                includes: q => q.Include(c => c.Class)
                                .Include(c => c.Student).ThenInclude(s => s.User)
            );

            if (assigns == null || !assigns.Any())
                throw new KeyNotFoundException("Không tìm thấy thông tin đăng ký cho lớp học này.");

            // Filter based on user role:
            // If user is Student: view own enrollment
            // Nếu user is Parent: view enrollment child

            ClassAssign? targetAssign = null;

            // Check if user is Student
            var studentProfileId = await _studentProfileService.GetStudentProfileIdByUserIdAsync(userId);
            if (studentProfileId != null)
            {
                targetAssign = assigns.FirstOrDefault(ca => ca.StudentId == studentProfileId);
            }
            else
            {
                // if not student, check if Parent
                foreach (var assign in assigns)
                {
                    if (assign.StudentId != null)
                    {
                        var isParent = await _parentRepo.ExistsLinkAsync(userId, assign.StudentId);
                        if (isParent)
                        {
                            targetAssign = assign;
                            break; // find first match
                        }
                    }
                }
            }

            if (targetAssign == null)
                throw new UnauthorizedAccessException("Bạn không có quyền xem thông tin đăng ký này hoặc bạn chưa đăng ký.");

            // 3. Map to DTO
            return new ClassAssignDetailDto
            {
                ClassAssignId = targetAssign.Id,
                ClassId = targetAssign.ClassId ?? string.Empty,
                ClassTitle = targetAssign.Class?.Title ?? "N/A",
                ClassDescription = targetAssign.Class?.Description,
                ClassSubject = targetAssign.Class?.Subject,
                ClassEducationLevel = targetAssign.Class?.EducationLevel,
                ClassPrice = targetAssign.Class?.Price ?? 0,
                ClassStatus = targetAssign.Class?.Status ?? ClassStatus.Pending,

                StudentId = targetAssign.StudentId ?? string.Empty,
                StudentName = targetAssign.Student?.User?.UserName ?? "N/A",
                StudentEmail = targetAssign.Student?.User?.Email,
                StudentPhone = targetAssign.Student?.User?.Phone,
                StudentAvatarUrl = targetAssign.Student?.User?.AvatarUrl,

                ApprovalStatus = targetAssign.ApprovalStatus,
                PaymentStatus = targetAssign.PaymentStatus,
                EnrolledAt = targetAssign.EnrolledAt,
                CreatedAt = targetAssign.CreatedAt,
                UpdatedAt = targetAssign.UpdatedAt
            };
        }

        /// <summary>
        /// confirm payment for class assignment
        /// Called when student pays for a Pending class to activate it immediately (1-1 class flow)
        /// </summary>
        public async Task<bool> ConfirmClassPaymentAsync(string actorUserId, string userRole, string classId)
        {
            // Validate Class
            var classEntity = await _uow.Classes.GetByIdAsync(classId);
            if (classEntity == null) throw new KeyNotFoundException("Lớp học không tồn tại.");

            if (classEntity.Status != ClassStatus.Pending)
                throw new InvalidOperationException($"Lớp học không ở trạng thái chờ thanh toán (Status: {classEntity.Status}).");

            // Validate Assign
            var assigns = await _uow.ClassAssigns.GetAllAsync(ca => ca.ClassId == classId);
            var targetAssign = assigns.FirstOrDefault(); // 1-1 class only has one assign

            if (targetAssign == null)
                throw new KeyNotFoundException("Không tìm thấy thông tin đăng ký của lớp này.");

            if (targetAssign.PaymentStatus == PaymentStatus.Paid)
                throw new InvalidOperationException("Lớp học này đã được thanh toán rồi.");

            // Verify ownership if caller is Student or Parent own that assign
            string studentIdInAssign = targetAssign.StudentId;
            string resolvedActorProfileId = await ResolveTargetStudentProfileIdAsync(actorUserId, userRole, studentIdInAssign);

            // Check ownership
            if (resolvedActorProfileId != studentIdInAssign)
            {
                throw new UnauthorizedAccessException("Bạn không có quyền thanh toán cho lớp học của học sinh này.");
            }

            // Lấy UserId của học sinh từ StudentProfileId để truyền vào PayEscrowRequest
            var studentProfile = await _mainUow.StudentProfiles.GetByIdAsync(targetAssign.StudentId);
            if (studentProfile == null || string.IsNullOrEmpty(studentProfile.UserId))
                throw new KeyNotFoundException("Không tìm thấy thông tin học sinh.");

            var studentUserId = studentProfile.UserId;

            // Gọi PayEscrowAsync để tạo escrow record, chuyển tiền vào ví admin và cập nhật PaymentStatus = Paid
            // PayEscrowAsync sẽ tự quản lý transaction riêng của nó
            // Truyền ClassAssignId để tránh query lại và tracking conflict
            // Offline class: thu phí kết nối 50,000 VND, đánh dấu Paid ngay
            if (classEntity.Mode == ClassMode.Offline)
            {
                const decimal CONNECTION_FEE = 50000m; // Phí kết nối: 50,000 VND
                
                // Đảm bảo wallets đã tồn tại
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

                // Kiểm tra số dư ví học sinh
                if (studentWallet.IsFrozen)
                    throw new InvalidOperationException("Ví của bạn đã bị khóa. Vui lòng liên hệ admin.");

                if (studentWallet.Balance < CONNECTION_FEE)
                    throw new InvalidOperationException($"Số dư không đủ. Cần {CONNECTION_FEE:N0} VND (phí kết nối), hiện có {studentWallet.Balance:N0} VND.");

                // Trừ phí kết nối từ ví học sinh và cộng vào ví admin
                studentWallet.Balance -= CONNECTION_FEE;
                adminWallet.Balance += CONNECTION_FEE;

                await _mainUow.Wallets.Update(studentWallet);
                await _mainUow.Wallets.Update(adminWallet);

                // Tạo transaction records
                await _mainUow.Transactions.AddAsync(new Transaction
                {
                    WalletId = studentWallet.Id,
                    Type = TransactionType.PayEscrow, // Dùng PayEscrow type cho phí kết nối
                    Status = TransactionStatus.Succeeded,
                    Amount = -CONNECTION_FEE,
                    Note = $"Phí kết nối lớp học offline {classId}",
                    CounterpartyUserId = _systemWalletOptions.SystemWalletUserId
                });

                await _mainUow.Transactions.AddAsync(new Transaction
                {
                    WalletId = adminWallet.Id,
                    Type = TransactionType.EscrowIn, // Dùng EscrowIn type
                    Status = TransactionStatus.Succeeded,
                    Amount = CONNECTION_FEE,
                    Note = $"Nhận phí kết nối lớp học offline {classId} từ học sinh",
                    CounterpartyUserId = studentUserId
                });

                targetAssign.PaymentStatus = PaymentStatus.Paid;
                await _uow.ClassAssigns.UpdateAsync(targetAssign);
                await _mainUow.SaveChangesAsync();
                await _uow.SaveChangesAsync();
            }
            else
            {
                var payEscrowResult = await _escrowService.PayEscrowAsync(actorUserId, new PayEscrowRequest
                {
                    ClassId = classId,
                    PayerStudentUserId = studentUserId, // UserId của học sinh (để PayEscrowAsync tìm đúng StudentProfileId)
                    ClassAssignId = targetAssign.Id // Truyền ClassAssignId để dùng trực tiếp entity đã được track
                });

                if (payEscrowResult.Status != "Ok")
                {
                    throw new InvalidOperationException($"Thanh toán escrow thất bại: {payEscrowResult.Message}");
                }
            }

            // Cập nhật Class status sang Ongoing (1-1 class từ Request -> paid -> set Ongoing)
            var executionStrategy = _context.Database.CreateExecutionStrategy();
            return await executionStrategy.ExecuteAsync(async () =>
            {
                using var transaction = await _context.Database.BeginTransactionAsync();
                try
                {
                    // Reload class entity để có dữ liệu mới nhất
                    classEntity = await _uow.Classes.GetByIdAsync(classId);
                    if (classEntity == null)
                        throw new KeyNotFoundException("Lớp học không tồn tại.");

                    // Tính lại CurrentStudentCount dựa trên số ClassAssign thực tế có PaymentStatus = Paid
                    // (sau khi thanh toán, PaymentStatus đã được set thành Paid)
                    // Reload ClassAssigns để đảm bảo có dữ liệu mới nhất từ PayEscrowAsync
                    var actualPaidCount = await _context.ClassAssigns
                        .CountAsync(ca => ca.ClassId == classId && 
                                         ca.PaymentStatus == PaymentStatus.Paid && 
                                         ca.DeletedAt == null);
                    var oldCount = classEntity.CurrentStudentCount;
                    classEntity.CurrentStudentCount = actualPaidCount;
                    Console.WriteLine($"[PayEscrowForExistingClassAsync] Cập nhật CurrentStudentCount cho lớp {classId}: {oldCount} → {actualPaidCount} (sau khi thanh toán)");
                    
                    // Debug: Log tất cả ClassAssigns của lớp này để kiểm tra
                    var allClassAssigns = await _context.ClassAssigns
                        .Where(ca => ca.ClassId == classId)
                        .Select(ca => new { ca.StudentId, ca.PaymentStatus, ca.DeletedAt })
                        .ToListAsync();
                    Console.WriteLine($"[PayEscrowForExistingClassAsync] Tất cả ClassAssigns của lớp {classId}:");
                    foreach (var ca in allClassAssigns)
                    {
                        Console.WriteLine($"  - StudentId: {ca.StudentId}, PaymentStatus: {ca.PaymentStatus}, DeletedAt: {ca.DeletedAt}");
                    }

                    // Logic chuyển sang Ongoing:
                    // - Lớp ONLINE: Cần học sinh thanh toán + gia sư đặt cọc (KHÔNG CẦN đợi ngày bắt đầu)
                    // - Lớp OFFLINE: Cần đến ngày bắt đầu + có học sinh thanh toán
                    if (classEntity.Mode == ClassMode.Online)
                    {
                        // Lớp ONLINE: Kiểm tra học sinh thanh toán + gia sư đặt cọc
                        var hasPaidStudents = classEntity.CurrentStudentCount > 0;
                        var tutorDeposit = await _mainUow.TutorDepositEscrows.GetByClassIdAsync(classEntity.Id);
                        var hasTutorDeposit = tutorDeposit != null && tutorDeposit.Status == TutorDepositStatus.Held;
                        
                        Console.WriteLine($"[ConfirmClassPaymentAsync] Kiểm tra điều kiện chuyển sang Ongoing cho lớp ONLINE {classId}:");
                        Console.WriteLine($"  - HasPaidStudents: {hasPaidStudents} (CurrentStudentCount: {classEntity.CurrentStudentCount})");
                        Console.WriteLine($"  - HasTutorDeposit: {hasTutorDeposit} (Deposit Status: {tutorDeposit?.Status})");
                        
                        if (hasPaidStudents && hasTutorDeposit)
                        {
                            var oldStatus = classEntity.Status;
                            classEntity.Status = ClassStatus.Ongoing;
                            await _uow.Classes.UpdateAsync(classEntity);
                            Console.WriteLine($"[ConfirmClassPaymentAsync] ✅ Lớp {classId} chuyển từ {oldStatus} → Ongoing (học sinh đã thanh toán + gia sư đã đặt cọc)");
                            
                            // Sinh lesson cho flow 1-1 sau khi lớp chuyển sang Ongoing
                            await GenerateLessonsIfNeededAsync(classEntity);
                        }
                        else
                        {
                            // Chưa đủ điều kiện → đảm bảo vẫn ở Pending
                            if (classEntity.Status != ClassStatus.Pending)
                            {
                                classEntity.Status = ClassStatus.Pending;
                            }
                            await _uow.Classes.UpdateAsync(classEntity);
                            Console.WriteLine($"[ConfirmClassPaymentAsync] ⏳ Lớp {classId} vẫn ở Pending (chưa đủ điều kiện: hasPaidStudents={hasPaidStudents}, hasTutorDeposit={hasTutorDeposit})");
                            
                            // Vẫn sinh lịch ngay (không cần đợi đến ngày bắt đầu)
                            await GenerateLessonsOnPaymentAsync(classEntity);
                        }
                    }
                    else
                    {
                        // Lớp OFFLINE: Chỉ cần có học sinh thanh toán → chuyển sang Ongoing ngay (KHÔNG CẦN đợi ngày bắt đầu)
                        var hasPaidStudents = classEntity.CurrentStudentCount > 0;
                        
                        Console.WriteLine($"[ConfirmClassPaymentAsync] Kiểm tra điều kiện chuyển sang Ongoing cho lớp OFFLINE {classId}:");
                        Console.WriteLine($"  - HasPaidStudents: {hasPaidStudents} (CurrentStudentCount: {classEntity.CurrentStudentCount})");
                        
                        if (hasPaidStudents)
                        {
                            var oldStatus = classEntity.Status;
                            classEntity.Status = ClassStatus.Ongoing;
                            await _uow.Classes.UpdateAsync(classEntity);
                            Console.WriteLine($"[ConfirmClassPaymentAsync] ✅ Lớp OFFLINE {classId} chuyển từ {oldStatus} → Ongoing (học sinh đã thanh toán)");
                            
                            // Sinh lesson cho flow 1-1 sau khi lớp chuyển sang Ongoing
                            await GenerateLessonsIfNeededAsync(classEntity);
                        }
                        else
                        {
                            // Chưa có học sinh thanh toán → đảm bảo vẫn ở Pending
                            if (classEntity.Status != ClassStatus.Pending)
                            {
                                classEntity.Status = ClassStatus.Pending;
                            }
                            await _uow.Classes.UpdateAsync(classEntity);
                            Console.WriteLine($"[ConfirmClassPaymentAsync] ⏳ Lớp OFFLINE {classId} vẫn ở Pending (chưa có học sinh thanh toán)");
                            
                            // Vẫn sinh lịch ngay (không cần đợi đến ngày bắt đầu)
                            await GenerateLessonsOnPaymentAsync(classEntity);
                        }
                    }

                    await _uow.SaveChangesAsync();
                    await _mainUow.SaveChangesAsync();
                    await transaction.CommitAsync();

                    // PayEscrowAsync đã gửi notification EscrowPaid cho payer rồi
                    // Chỉ cần gửi thêm notification ClassEnrollmentSuccess
                    try
                    {
                        await _notificationService.CreateAccountNotificationAsync(
                           actorUserId,
                           NotificationType.ClassEnrollmentSuccess,
                           $"Thanh toán thành công. Lớp học {classEntity.Title} đã chính thức bắt đầu.",
                           classEntity.Id);
                        await _uow.SaveChangesAsync();

                        // Tạo conversation và gửi notification về match/tạo nhóm
                        var tutorProfile = await _mainUow.TutorProfiles.GetByIdAsync(classEntity.TutorId);
                        var tutorUserId = tutorProfile?.UserId;
                        var targetAssign = assigns.FirstOrDefault();
                        if (targetAssign != null)
                        {
                            var studentProfile = await _mainUow.StudentProfiles.GetByIdAsync(targetAssign.StudentId);
                            var studentUserId = studentProfile?.UserId;

                            if (!string.IsNullOrEmpty(tutorUserId) && !string.IsNullOrEmpty(studentUserId))
                            {
                                // Nếu là lớp 1-1 (StudentLimit = 1) → tạo conversation 1-1
                                if (classEntity.StudentLimit == 1)
                                {
                                    try
                                    {
                                        await _conversationService.GetOrCreateOneToOneConversationAsync(tutorUserId, studentUserId);
                                        
                                        // Gửi notification cho tutor
                                        var tutorNotification = await _notificationService.CreateAccountNotificationAsync(
                                            tutorUserId,
                                            NotificationType.ClassEnrollmentSuccess,
                                            $"Học sinh đã thanh toán cho lớp {classEntity.Title}. Bạn đã được match với học sinh.",
                                            classEntity.Id);
                                        await _notificationService.SendRealTimeNotificationAsync(tutorUserId, tutorNotification);

                                        // Gửi notification cho student
                                        var studentNotif = await _notificationService.CreateAccountNotificationAsync(
                                            studentUserId,
                                            NotificationType.ClassEnrollmentSuccess,
                                            $"Bạn đã được match với gia sư cho lớp {classEntity.Title}. Hãy bắt đầu trò chuyện!",
                                            classEntity.Id);
                                        await _notificationService.SendRealTimeNotificationAsync(studentUserId, studentNotif);
                                    }
                                    catch (Exception convEx)
                                    {
                                        Console.WriteLine($"Tạo conversation 1-1 thất bại: {convEx.Message}");
                                    }
                                }
                                // Nếu là lớp group (StudentLimit > 1) → tạo hoặc thêm vào group conversation
                                else if (classEntity.StudentLimit > 1)
                                {
                                    try
                                    {
                                        // Tạo hoặc lấy group conversation, tự động thêm student vào
                                        await _conversationService.GetOrCreateClassConversationAsync(classEntity.Id, studentUserId);
                                        
                                        // Gửi notification cho tutor (chỉ khi học sinh đầu tiên vào)
                                        if (classEntity.CurrentStudentCount == 1)
                                        {
                                            var tutorNotification = await _notificationService.CreateAccountNotificationAsync(
                                                tutorUserId,
                                                NotificationType.ClassEnrollmentSuccess,
                                                $"Nhóm lớp {classEntity.Title} đã được tạo. Học sinh đầu tiên đã tham gia.",
                                                classEntity.Id);
                                            await _notificationService.SendRealTimeNotificationAsync(tutorUserId, tutorNotification);
                                        }
                                        else
                                        {
                                            var tutorNotification = await _notificationService.CreateAccountNotificationAsync(
                                                tutorUserId,
                                                NotificationType.ClassEnrollmentSuccess,
                                                $"Có học sinh mới tham gia vào nhóm lớp {classEntity.Title}.",
                                                classEntity.Id);
                                            await _notificationService.SendRealTimeNotificationAsync(tutorUserId, tutorNotification);
                                        }

                                        // Gửi notification cho student
                                        var studentNotif = await _notificationService.CreateAccountNotificationAsync(
                                            studentUserId,
                                            NotificationType.ClassEnrollmentSuccess,
                                            $"Bạn đã tham gia vào nhóm lớp {classEntity.Title}. Hãy bắt đầu trò chuyện với gia sư và các học sinh khác!",
                                            classEntity.Id);
                                        await _notificationService.SendRealTimeNotificationAsync(studentUserId, studentNotif);
                                    }
                                    catch (Exception convEx)
                                    {
                                        Console.WriteLine($"Tạo group conversation thất bại: {convEx.Message}");
                                    }
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        // Log error nhưng không throw
                        Console.WriteLine($"Gửi thông báo thất bại: {ex.Message}");
                    }

                    return true;
                }
                catch (Exception)
                {
                    await transaction.RollbackAsync();
                    throw;
                }
            });
        }
        #endregion

        #region Tutor actions
        public async Task<List<RelatedResourceDto>> GetMyTutorsAsync(string actorUserId, string userRole, string? studentId)
        {
            // take student profile id
            string targetStudentId = await ResolveTargetStudentProfileIdAsync(actorUserId, userRole, studentId);

            // Check null
            if (targetStudentId == null) return new List<RelatedResourceDto>();

            var tutors = await _context.ClassAssigns
                .Include(ca => ca.Class)
                    .ThenInclude(c => c.Tutor)
                        .ThenInclude(t => t.User)
                .Where(ca => ca.StudentId == targetStudentId
                             && ca.Class.Status != ClassStatus.Cancelled)
                .Select(ca => ca.Class.Tutor)
                .Distinct()
                .ToListAsync();

            return tutors.Select(t => new RelatedResourceDto
            {
                ProfileId = t.Id.ToString(), // TutorId thường là chuỗi
                UserId = t.UserId,
                FullName = t.User?.UserName ?? t.User?.UserName ?? "N/A",
                AvatarUrl = t.User?.AvatarUrl,
                Email = t.User?.Email,
                Phone = t.User?.Phone
            }).ToList();
        }

        public async Task<List<StudentEnrollmentDto>> GetStudentsInClassAsync(string tutorUserId, string classId)
        {
            // Verify tutor owns the class
            var tutorProfileId = await _tutorProfileService.GetTutorProfileIdByUserIdAsync(tutorUserId);
            if (tutorProfileId == null)
                throw new UnauthorizedAccessException("Tài khoản gia sư không hợp lệ.");

            var targetClass = await _uow.Classes.GetByIdAsync(classId);
            if (targetClass == null)
                throw new KeyNotFoundException($"Không tìm thấy lớp học với ID '{classId}'.");

            if (targetClass.TutorId != tutorProfileId)
                throw new UnauthorizedAccessException("Bạn không có quyền xem học sinh của lớp học này.");

            // Get all enrollments for this class
            var classAssigns = await _uow.ClassAssigns.GetByClassIdAsync(classId, includeStudent: true);

            return classAssigns.Select(ca => new StudentEnrollmentDto
            {
                StudentId = ca.StudentId ?? string.Empty,
                StudentUserId = ca.Student?.UserId,
                StudentName = ca.Student?.User?.UserName ?? "N/A",
                StudentEmail = ca.Student?.User?.Email,
                StudentAvatarUrl = ca.Student?.User?.AvatarUrl,
                StudentPhone = ca.Student?.User?.Phone,
                ApprovalStatus = ca.ApprovalStatus,
                PaymentStatus = ca.PaymentStatus,
                EnrolledAt = ca.EnrolledAt,
                CreatedAt = ca.CreatedAt
            }).ToList();
        }

        public async Task<List<StudentEnrollmentDto>> GetStudentsInClassForAdminAsync(string classId)
        {
            // Admin can view students in any class, no ownership verification needed
            var targetClass = await _uow.Classes.GetByIdAsync(classId);
            if (targetClass == null)
                throw new KeyNotFoundException($"Không tìm thấy lớp học với ID '{classId}'.");

            // Get all enrollments for this class - chỉ lấy học sinh đã thanh toán và đã được phê duyệt
            var allClassAssigns = await _uow.ClassAssigns.GetByClassIdAsync(classId, includeStudent: true);
            
            // Filter: chỉ lấy học sinh đã thanh toán (PaymentStatus = Paid) và đã được phê duyệt (ApprovalStatus = Approved)
            // Loại bỏ học sinh đã bị hủy (PaymentStatus = Refunded)
            var activeClassAssigns = allClassAssigns
                .Where(ca => ca.PaymentStatus == PaymentStatus.Paid 
                          && ca.ApprovalStatus == ApprovalStatus.Approved
                          && ca.DeletedAt == null) // Chỉ lấy học sinh chưa bị xóa
                .ToList();

            return activeClassAssigns.Select(ca => new StudentEnrollmentDto
            {
                StudentId = ca.StudentId ?? string.Empty,
                StudentUserId = ca.Student?.UserId,
                StudentName = ca.Student?.User?.UserName ?? "N/A",
                StudentEmail = ca.Student?.User?.Email,
                StudentAvatarUrl = ca.Student?.User?.AvatarUrl,
                StudentPhone = ca.Student?.User?.Phone,
                ApprovalStatus = ca.ApprovalStatus,
                PaymentStatus = ca.PaymentStatus,
                EnrolledAt = ca.EnrolledAt,
                CreatedAt = ca.CreatedAt
            }).ToList();
        }

        public async Task<List<RelatedResourceDto>> GetMyStudentsAsync(string tutorUserId)
        {
            var tutorProfileId = await _tutorProfileService.GetTutorProfileIdByUserIdAsync(tutorUserId);

            // tutorProfileId string?, check null
            if (string.IsNullOrEmpty(tutorProfileId)) return new List<RelatedResourceDto>();

            var students = await _context.ClassAssigns
                .Include(ca => ca.Class)
                .Include(ca => ca.Student)
                    .ThenInclude(s => s.User)
                .Where(ca => ca.Class.TutorId == tutorProfileId
                             && ca.Class.Status != ClassStatus.Cancelled)
                .Select(ca => ca.Student)
                .Distinct()
                .ToListAsync();

            return students.Select(s => new RelatedResourceDto
            {
                ProfileId = s.Id.ToString(),
                UserId = s.UserId,
                FullName = s.User?.UserName ?? s.User?.UserName ?? "N/A",
                AvatarUrl = s.User?.AvatarUrl,
                Email = s.User?.Email,
                Phone = s.User?.Phone
            }).ToList();
        }
        #endregion

        // --- Helper ---
        private async Task<string> ResolveTargetStudentProfileIdAsync(string actorUserId, string userRole, string? inputStudentId)
        {
            if (userRole == "Student")
            {
                var profileId = await _studentProfileService.GetStudentProfileIdByUserIdAsync(actorUserId);
                if (string.IsNullOrEmpty(profileId))
                    throw new KeyNotFoundException("Không tìm thấy hồ sơ học sinh của bạn.");
                return profileId;
            }
            else if (userRole == "Parent")
            {
                if (string.IsNullOrEmpty(inputStudentId))
                    throw new ArgumentException("Phụ huynh cần chọn học sinh để đăng ký.");

                // Validate Parent-Child Link
                // inputStudentId ở đây là ID của bảng StudentProfile
                var isLinked = await _parentRepo.ExistsLinkAsync(actorUserId, inputStudentId);
                if (!isLinked)
                    throw new UnauthorizedAccessException("Bạn không có quyền đăng ký cho học sinh này.");

                return inputStudentId;
            }
            throw new UnauthorizedAccessException("Role không hợp lệ.");
        }

        /// <summary>
        /// Helper: Refund escrow trong transaction (không gọi SaveChanges)
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

            esc = escReloaded;

            decimal refundAmount;
            if (esc.Status == EscrowStatus.Held)
            {
                // Refund full
                refundAmount = esc.GrossAmount;
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

            if (adminWallet.Balance < refundAmount)
            {
                Console.WriteLine($"RefundEscrowInTransactionAsync: ❌ Số dư admin không đủ. Số dư: {adminWallet.Balance:N0}, Cần: {refundAmount:N0}");
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
                esc.Status = EscrowStatus.Refunded;
                esc.RefundedAt = DateTimeHelper.VietnamNow;
            }
            await _mainUow.Escrows.UpdateAsync(esc);

            // KHÔNG gọi SaveChanges ở đây - để WithdrawFromClassAsync gọi một lần ở cuối transaction
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

            // KHÔNG gọi SaveChanges ở đây - để WithdrawFromClassAsync gọi một lần ở cuối transaction
            return true;
        }

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