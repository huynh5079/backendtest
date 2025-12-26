using BusinessLayer.DTOs.Schedule.ClassRequest;
using BusinessLayer.DTOs.Schedule.TutorApplication;
using BusinessLayer.Helper;
using BusinessLayer.Options;
using BusinessLayer.Service.Interface;
using BusinessLayer.Service.Interface.IScheduleService;
using DataLayer.Entities;
using DataLayer.Enum;
using DataLayer.Repositories.Abstraction;
using DataLayer.Repositories.Abstraction.Schedule;
using DataLayer.Repositories.GenericType;
using DataLayer.Repositories.GenericType.Abstraction;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using System;
using System.Linq;
using System.Linq.Expressions;

namespace BusinessLayer.Service.ScheduleService;

public class ClassRequestService : IClassRequestService
{
    private readonly IScheduleUnitOfWork _uow;
    private readonly IUnitOfWork _mainUow;
    private readonly IStudentProfileService _studentProfileService;
    private readonly ITutorProfileService _tutorProfileService;
    private readonly TpeduContext _context;
    private readonly IParentProfileRepository _parentRepo;
    private readonly IScheduleGenerationService _scheduleGenerationService;
    private readonly INotificationService _notificationService;
    private readonly SystemWalletOptions _systemWalletOptions;

    public ClassRequestService(
        IScheduleUnitOfWork uow,
        IUnitOfWork mainUow,
        IStudentProfileService studentProfileService,
        ITutorProfileService tutorProfileService,
        TpeduContext context,
        IParentProfileRepository parentRepo,
        IScheduleGenerationService scheduleGenerationService,
        INotificationService notificationService,
        IOptions<SystemWalletOptions> systemWalletOptions)
    {
        _uow = uow;
        _mainUow = mainUow;
        _studentProfileService = studentProfileService;
        _tutorProfileService = tutorProfileService;
        _context = context;
        _parentRepo = parentRepo;
        _scheduleGenerationService = scheduleGenerationService;
        _notificationService = notificationService;
        _systemWalletOptions = systemWalletOptions.Value;
    }

    #region Student's Actions
    public async Task<ClassRequestResponseDto?> CreateClassRequestAsync(string actorUserId, string userRole, CreateClassRequestDto dto)
    {
        var targetStudentProfileId = await ResolveTargetStudentProfileIdAsync(actorUserId, userRole, dto.StudentUserId);

        // Phí tạo request: 30,000 VND
        const decimal REQUEST_CREATION_FEE = 50000m;
        
        // Phí kết nối khi tutor chấp nhận offline request: 30,000 VND (giống phí tạo request)
        const decimal CONNECTION_FEE = 50000m;

        // Lấy studentUserId từ targetStudentProfileId
        var studentProfile = await _mainUow.StudentProfiles.GetAsync(
            filter: s => s.Id == targetStudentProfileId,
            includes: q => q.Include(s => s.User));
        if (studentProfile?.User == null || string.IsNullOrWhiteSpace(studentProfile.User.Id))
        {
            throw new UnauthorizedAccessException("Không tìm thấy thông tin học sinh.");
        }
        string studentUserId = studentProfile.User.Id;

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

        // Kiểm tra số dư ví học sinh để trả phí tạo request
        if (studentWallet.IsFrozen)
        {
            throw new InvalidOperationException("Ví bị khóa, không thể tạo yêu cầu.");
        }
        if (studentWallet.Balance < REQUEST_CREATION_FEE)
        {
            throw new InvalidOperationException($"Số dư không đủ để tạo yêu cầu. Cần {REQUEST_CREATION_FEE:N0} VND, hiện có {studentWallet.Balance:N0} VND.");
        }

        var executionStrategy = _context.Database.CreateExecutionStrategy();
        var newRequest = new ClassRequest();

        await executionStrategy.ExecuteAsync(async () =>
        {
            await using var transaction = await _context.Database.BeginTransactionAsync();
            try
            {
                // create ClassRequest
                newRequest = new ClassRequest
                {
                    Id = Guid.NewGuid().ToString(),
                    StudentId = targetStudentProfileId, // Validated with helper below
                    TutorId = dto.TutorId, // null -> "Marketplace", ID -> "Direct"
                    Budget = dto.Budget,
                    Status = ClassRequestStatus.Pending, // Pending
                    Mode = dto.Mode,
                    ExpiryDate = DateTimeHelper.VietnamNow.AddDays(7), // set 7 days
                    Description = dto.Description,
                    Location = dto.Location,
                    SpecialRequirements = dto.SpecialRequirements,
                    Subject = dto.Subject,
                    EducationLevel = dto.EducationLevel,
                    ClassStartDate = dto.ClassStartDate?.ToUniversalTime(),
                    OnlineStudyLink = dto.OnlineStudyLink
                };
                await _uow.ClassRequests.CreateAsync(newRequest); // no Save

                // create ClassRequestSchedules
                var newSchedules = dto.Schedules.Select(s => new ClassRequestSchedule
                {
                    Id = Guid.NewGuid().ToString(),
                    ClassRequestId = newRequest.Id,
                    DayOfWeek = s.DayOfWeek,
                    StartTime = s.StartTime,
                    EndTime = s.EndTime
                }).ToList();

                await _context.ClassRequestSchedules.AddRangeAsync(newSchedules); // Unsave

                // Thu phí tạo request: Trừ từ ví học sinh, cộng vào ví admin
                studentWallet.Balance -= REQUEST_CREATION_FEE;
                adminWallet.Balance += REQUEST_CREATION_FEE;

                await _mainUow.Wallets.Update(studentWallet);
                await _mainUow.Wallets.Update(adminWallet);

                // Ghi transaction cho học sinh (trừ phí)
                await _mainUow.Transactions.AddAsync(new Transaction
                {
                    WalletId = studentWallet.Id,
                    Type = TransactionType.PayoutOut, // Phí tạo request
                    Status = TransactionStatus.Succeeded,
                    Amount = -REQUEST_CREATION_FEE,
                    Note = $"Phí tạo yêu cầu lớp học {newRequest.Id}",
                    CounterpartyUserId = _systemWalletOptions.SystemWalletUserId
                });

                // Ghi transaction cho admin (nhận phí - doanh thu hệ thống)
                await _mainUow.Transactions.AddAsync(new Transaction
                {
                    WalletId = adminWallet.Id,
                    Type = TransactionType.PayoutIn, // Doanh thu từ phí tạo request
                    Status = TransactionStatus.Succeeded,
                    Amount = REQUEST_CREATION_FEE,
                    Note = $"Phí tạo yêu cầu từ học sinh {studentUserId} - Request {newRequest.Id}",
                    CounterpartyUserId = studentUserId
                });

                // 3. Save
                await _uow.SaveChangesAsync();
                await _mainUow.SaveChangesAsync();
                await transaction.CommitAsync();

                // Gửi notification cho tutor nếu là direct request (có TutorId)
                if (!string.IsNullOrEmpty(dto.TutorId))
                {
                    var tutorProfile = await _mainUow.TutorProfiles.GetByIdAsync(dto.TutorId);
                    if (tutorProfile != null && !string.IsNullOrEmpty(tutorProfile.UserId))
                    {
                        try
                        {
                            var studentProfile = await _mainUow.StudentProfiles.GetAsync(
                                filter: s => s.Id == targetStudentProfileId,
                                includes: q => q.Include(s => s.User));
                            var studentName = studentProfile?.User?.UserName ?? "một học sinh";
                            
                            // Tạo message notification
                            string notificationMessage;
                            if (dto.Mode == ClassMode.Offline && !string.IsNullOrEmpty(dto.Location))
                            {
                                // Nếu là offline, thêm địa chỉ học sinh vào notification
                                notificationMessage = $"{studentName} đã gửi yêu cầu lớp học '{newRequest.Subject}' cho bạn (Offline). Địa chỉ: {dto.Location}. Vui lòng xem xét và phản hồi.";
                            }
                            else
                            {
                                notificationMessage = $"{studentName} đã gửi yêu cầu lớp học '{newRequest.Subject}' cho bạn. Vui lòng xem xét và phản hồi.";
                            }
                            
                            var notification = await _notificationService.CreateAccountNotificationAsync(
                                tutorProfile.UserId,
                                NotificationType.ClassRequestReceived,
                                notificationMessage,
                                newRequest.Id);
                            await _uow.SaveChangesAsync();
                            await _notificationService.SendRealTimeNotificationAsync(tutorProfile.UserId, notification);
                        }
                        catch (Exception notifEx)
                        {
                            System.Diagnostics.Debug.WriteLine($"Failed to send notification: {notifEx.Message}");
                        }
                    }
                }
            }
            catch (Exception)
            {
                await transaction.RollbackAsync();
                throw;
            }
        });

        return await GetClassRequestByIdAsync(newRequest.Id);
    }
    public async Task<ClassRequestResponseDto?> UpdateClassRequestAsync(string actorUserId, string userRole, string requestId, UpdateClassRequestDto dto)
    {
        try
        {
            var request = await ValidateAndGetRequestAsync(actorUserId, userRole, requestId);

            // Only fixable with "Pending"
            if (request.Status != ClassRequestStatus.Pending)
                throw new InvalidOperationException($"Không thể sửa yêu cầu ở trạng thái '{request.Status}'.");

            // Update only provided fields
            if (!string.IsNullOrEmpty(dto.Description))
                request.Description = dto.Description ?? request.Description;
            if (dto.Location != null)
                request.Location = dto.Location;
            if (!string.IsNullOrEmpty(dto.SpecialRequirements))
                request.SpecialRequirements = dto.SpecialRequirements ?? request.SpecialRequirements;
            if (dto.Budget.HasValue)
                request.Budget = dto.Budget ?? request.Budget;
            request.OnlineStudyLink = dto.OnlineStudyLink ?? request.OnlineStudyLink;
            request.Mode = dto.Mode ?? request.Mode;
            request.ClassStartDate = dto.ClassStartDate?.ToUniversalTime() ?? request.ClassStartDate;

            await _uow.ClassRequests.UpdateAsync(request); // Unsave
            await _uow.SaveChangesAsync(); // Save

            return await GetClassRequestByIdAsync(requestId);
        }
        catch (Exception)
        {
            return null;
        }
    }
    public async Task<bool> UpdateClassRequestScheduleAsync(string actorUserId, string userRole, string requestId, List<ClassRequestScheduleDto> scheduleDtos)
    {
        var request = await ValidateAndGetRequestAsync(actorUserId, userRole, requestId);

        if (request == null)
            throw new KeyNotFoundException("Không tìm thấy yêu cầu hoặc bạn không có quyền sửa.");

        if (request.Status != ClassRequestStatus.Pending)
            throw new InvalidOperationException($"Không thể sửa lịch của yêu cầu ở trạng thái '{request.Status}'.");

        // transaction 
        var executionStrategy = _context.Database.CreateExecutionStrategy();
        await executionStrategy.ExecuteAsync(async () =>
        {
            await using var transaction = await _context.Database.BeginTransactionAsync();
            try
            {
                // delete old schedules
                var oldSchedules = await _context.ClassRequestSchedules
                    .Where(crs => crs.ClassRequestId == requestId)
                    .ToListAsync();

                _context.ClassRequestSchedules.RemoveRange(oldSchedules);

                // add new schedules
                var newSchedules = scheduleDtos.Select(s => new ClassRequestSchedule
                {
                    Id = Guid.NewGuid().ToString(),
                    ClassRequestId = requestId,
                    DayOfWeek = s.DayOfWeek,
                    StartTime = s.StartTime,
                    EndTime = s.EndTime
                }).ToList();

                await _context.ClassRequestSchedules.AddRangeAsync(newSchedules);

                // 3. Save
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
    public async Task<bool> CancelClassRequestAsync(string actorUserId, string userRole, string requestId)
    {
        try
        {
            // Validate 
            var request = await ValidateAndGetRequestAsync(actorUserId, userRole, requestId);

            // Only cancel with "Pending"
            if (request.Status != ClassRequestStatus.Pending)
                throw new InvalidOperationException($"Không thể hủy yêu cầu ở trạng thái '{request.Status}'.");

            request.Status = ClassRequestStatus.Cancelled;

            await _uow.ClassRequests.UpdateAsync(request); // Unsave
            await _uow.SaveChangesAsync(); // Save

            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }
    public async Task<IEnumerable<ClassRequestResponseDto>> GetMyClassRequestsAsync(string actorUserId, string userRole, string? specificChildId = null)
    {
        List<string> studentProfileIds = new();

        if (userRole == "Student")
        {
            var sid = await _studentProfileService.GetStudentProfileIdByUserIdAsync(actorUserId);
            if (sid != null) studentProfileIds.Add(sid);
        }
        else if (userRole == "Parent")
        {
            // Parent: take all children or specific child
            if (!string.IsNullOrEmpty(specificChildId))
            {
                // if specific child, validate link
                var isLinked = await _parentRepo.ExistsLinkAsync(actorUserId, specificChildId);
                if (!isLinked)
                    throw new UnauthorizedAccessException("Bạn không có quyền xem yêu cầu của học sinh này.");

                studentProfileIds.Add(specificChildId);
            }
            else
            {
                var childrenIds = await _parentRepo.GetChildrenIdsAsync(actorUserId);
                studentProfileIds.AddRange(childrenIds);
            }
        }

        if (!studentProfileIds.Any())
            return new List<ClassRequestResponseDto>(); // return empty

        // Lấy tất cả ClassRequest của học sinh (trừ những request đã bị xóa)
        // Bao gồm: Pending, Matched, Cancelled (để học sinh có thể xem lịch sử)
        var requests = await _uow.ClassRequests.GetAllAsync(
            filter: cr => studentProfileIds.Contains(cr.StudentId!) 
                       && cr.DeletedAt == null,
            includes: q => q.Include(cr => cr.Student).ThenInclude(s => s.User)
                            .Include(cr => cr.Tutor).ThenInclude(t => t.User)
                            .Include(cr => cr.ClassRequestSchedules)
        );

        return requests
            .OrderByDescending(cr => cr.CreatedAt)
            .Select(cr => MapToResponseDto(cr));
    }

    #endregion

    #region Tutor's Actions
    public async Task<IEnumerable<ClassRequestResponseDto>> GetDirectRequestsAsync(string tutorUserId)
    {
        var tutorProfileId = await _tutorProfileService.GetTutorProfileIdByUserIdAsync(tutorUserId);
        if (tutorProfileId == null)
            return new List<ClassRequestResponseDto>();

        var requests = await _uow.ClassRequests.GetAllAsync(
            filter: cr => cr.TutorId == tutorProfileId && cr.Status == ClassRequestStatus.Pending,
            includes: q => q.Include(cr => cr.Student).ThenInclude(s => s.User)
                            .Include(cr => cr.Tutor).ThenInclude(t => t.User)
                            .Include(cr => cr.ClassRequestSchedules)
        );

        return requests
            .OrderByDescending(cr => cr.CreatedAt)
            .Select(cr => MapToResponseDto(cr));
    }

    public async Task<AcceptRequestResponseDto?> RespondToDirectRequestAsync(string tutorUserId, string requestId, bool accept, string? meetingLink = null)
    {
        var tutorProfileId = await _tutorProfileService.GetTutorProfileIdByUserIdAsync(tutorUserId);
        if (tutorProfileId == null)
            throw new UnauthorizedAccessException("Tài khoản gia sư không hợp lệ.");

        var request = await _uow.ClassRequests.GetAsync(
            cr => cr.Id == requestId && cr.TutorId == tutorProfileId,
            includes: q => q.Include(cr => cr.ClassRequestSchedules));

        if (request == null)
            throw new KeyNotFoundException("Không tìm thấy yêu cầu hoặc bạn không có quyền.");

        if (request.Status != ClassRequestStatus.Pending)
            throw new InvalidOperationException("Yêu cầu này đã được xử lý.");

        if (!accept)
        {
            // Reject: only update status
            request.Status = ClassRequestStatus.Rejected;
            await _uow.ClassRequests.UpdateAsync(request);
            await _uow.SaveChangesAsync();

            // Gửi notification cho student khi request bị reject
            var studentProfile = await _mainUow.StudentProfiles.GetByIdAsync(request.StudentId ?? string.Empty);
            if (studentProfile != null && !string.IsNullOrEmpty(studentProfile.UserId))
            {
                try
                {
                    var notification = await _notificationService.CreateAccountNotificationAsync(
                        studentProfile.UserId,
                        NotificationType.ClassRequestRejected,
                        "Yêu cầu lớp học của bạn đã bị gia sư từ chối.",
                        request.Id);
                    await _uow.SaveChangesAsync();
                    await _notificationService.SendRealTimeNotificationAsync(studentProfile.UserId, notification);
                }
                catch (Exception notifEx)
                {
                    System.Diagnostics.Debug.WriteLine($"Failed to send notification: {notifEx.Message}");
                }
            }

            return null; // Reject không tạo Class, trả về null
        }

        var executionStrategy = _context.Database.CreateExecutionStrategy();
        return await executionStrategy.ExecuteAsync<AcceptRequestResponseDto?>(async () =>
            {
                await using var transaction = await _context.Database.BeginTransactionAsync();
                try
                {
                    // Check duplicate class
                    await CheckForDuplicateClassFromRequestAsync(tutorProfileId, request);

                    bool isOffline = request.Mode == ClassMode.Offline;

                    // Nếu là offline, thu phí kết nối từ tutor
                    if (isOffline)
                    {
                        const decimal CONNECTION_FEE = 50000m; // Phí kết nối: 50,000 VND
                        
                        // Đảm bảo wallets đã tồn tại
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

                        // Kiểm tra số dư ví tutor
                        if (tutorWallet.IsFrozen)
                        {
                            throw new InvalidOperationException("Ví bị khóa, không thể chấp nhận yêu cầu.");
                        }
                        if (tutorWallet.Balance < CONNECTION_FEE)
                        {
                            throw new InvalidOperationException($"Số dư không đủ để chấp nhận yêu cầu. Cần {CONNECTION_FEE:N0} VND phí kết nối, hiện có {tutorWallet.Balance:N0} VND.");
                        }

                        // Thu phí kết nối: Trừ từ ví tutor, cộng vào ví admin
                        tutorWallet.Balance -= CONNECTION_FEE;
                        adminWallet.Balance += CONNECTION_FEE;

                        await _mainUow.Wallets.Update(tutorWallet);
                        await _mainUow.Wallets.Update(adminWallet);

                        // Ghi transaction cho tutor (trừ phí)
                        await _mainUow.Transactions.AddAsync(new Transaction
                        {
                            WalletId = tutorWallet.Id,
                            Type = TransactionType.PayoutOut, // Phí kết nối
                            Status = TransactionStatus.Succeeded,
                            Amount = -CONNECTION_FEE,
                            Note = $"Phí kết nối cho yêu cầu lớp học {request.Id}",
                            CounterpartyUserId = _systemWalletOptions.SystemWalletUserId
                        });

                        // Ghi transaction cho admin (nhận phí - doanh thu hệ thống)
                        await _mainUow.Transactions.AddAsync(new Transaction
                        {
                            WalletId = adminWallet.Id,
                            Type = TransactionType.PayoutIn, // Doanh thu từ phí kết nối
                            Status = TransactionStatus.Succeeded,
                            Amount = CONNECTION_FEE,
                            Note = $"Phí kết nối từ gia sư {tutorUserId} - Request {request.Id}",
                            CounterpartyUserId = tutorUserId
                        });
                    }

                    // Generate Class from Request
                    var newClass = new Class
                    {
                        Id = Guid.NewGuid().ToString(),
                        TutorId = tutorProfileId,
                        Title = $"Lớp {request.Subject} {request.EducationLevel} (1-1)",
                        Description = $"{request.Description}\n\n" +
                                      $"Yêu cầu đặc biệt: {request.SpecialRequirements}\n" +
                                      $"----------------\n" +
                                      $"[RefReqId:{request.Id}]",
                        Price = request.Budget,
                        // Chỉ set Ongoing nếu offline VÀ đã đến ngày bắt đầu, nếu không thì Pending
                        Status = (isOffline && (request.ClassStartDate == null || request.ClassStartDate <= DateTimeHelper.VietnamNow)) 
                            ? ClassStatus.Ongoing 
                            : ClassStatus.Pending, // Waiting for payment or start date
                        Location = request.Location,
                        Mode = request.Mode,
                        Subject = request.Subject,
                        EducationLevel = request.EducationLevel,
                        ClassStartDate = request.ClassStartDate,
                        StudentLimit = 1, // 1-1
                        CurrentStudentCount = 1,
                        CreatedAt = DateTimeHelper.VietnamNow,
                        UpdatedAt = DateTimeHelper.VietnamNow,
                        // link tranfer
                        OnlineStudyLink = !string.IsNullOrEmpty(meetingLink) ? meetingLink : request.OnlineStudyLink
                    };
                    await _uow.Classes.CreateAsync(newClass); // save ClassId for closure

                    // create ClassAssign vs PaymentStatus = Pending
                    var newAssignment = new ClassAssign
                    {
                        Id = Guid.NewGuid().ToString(),
                        ClassId = newClass.Id,
                        StudentId = request.StudentId,
                        PaymentStatus = isOffline ? PaymentStatus.Paid : PaymentStatus.Pending, // unpaid, will navigate to payment
                        ApprovalStatus = ApprovalStatus.Approved, // Tutor accepted, request = Approved
                        EnrolledAt = DateTimeHelper.VietnamNow
                    };
                    await _uow.ClassAssigns.CreateAsync(newAssignment);

                    // copy schedule from ClassRequestSchedules to ClassSchedules
                    var newClassSchedules = request.ClassRequestSchedules.Select(reqSchedule => new ClassSchedule
                    {
                        Id = Guid.NewGuid().ToString(),
                        ClassId = newClass.Id,
                        DayOfWeek = reqSchedule.DayOfWeek ?? 0,
                        StartTime = reqSchedule.StartTime,
                        EndTime = reqSchedule.EndTime
                    }).ToList();
                    await _context.ClassSchedules.AddRangeAsync(newClassSchedules);

                    // update request status
                    request.Status = ClassRequestStatus.Matched; // matched tutor and created class
                    await _uow.ClassRequests.UpdateAsync(request);

                    // call schedule generation
                    var scheduleEndDate = await _scheduleGenerationService.GenerateScheduleFromRequestAsync(
                        newClass.Id,
                        tutorProfileId,
                        request.ClassStartDate ?? DateTimeHelper.VietnamNow,
                        request.ClassRequestSchedules
                    );

                    // save all
                    await _uow.SaveChangesAsync();
                    await transaction.CommitAsync();

                    // Gửi notification cho student khi request được accept và class được tạo
                    var studentProfile = await _mainUow.StudentProfiles.GetByIdAsync(request.StudentId ?? string.Empty);
                    if (studentProfile != null && !string.IsNullOrEmpty(studentProfile.UserId))
                    {
                        try
                        {
                            string notiMessage;
                            string dateInfo = scheduleEndDate.HasValue
                                ? $" Dự kiến kết thúc vào {scheduleEndDate.Value.ToLocalTime():dd/MM/yyyy}."
                                : "";

                            string msgPrefix = isOffline
                            ? $"Gia sư đã chấp nhận yêu cầu (Offline).{dateInfo} Hãy liên hệ để bắt đầu học."
                            : $"Gia sư đã chấp nhận yêu cầu.{dateInfo} Vui lòng thanh toán để bắt đầu học.";

                            var acceptNotification = await _notificationService.CreateAccountNotificationAsync(
                                studentProfile.UserId,
                                NotificationType.ClassRequestAccepted,
                                "Yêu cầu lớp học của bạn đã được gia sư chấp nhận.",
                                request.Id);
                            await _uow.SaveChangesAsync();
                            await _notificationService.SendRealTimeNotificationAsync(studentProfile.UserId, acceptNotification);

                            var createdNotification = await _notificationService.CreateAccountNotificationAsync(
                                studentProfile.UserId,
                                NotificationType.ClassCreatedFromRequest,
                                msgPrefix,
                                newClass.Id); // Use newClass.Id as RelatedEntityId
                            await _uow.SaveChangesAsync();
                            await _notificationService.SendRealTimeNotificationAsync(studentProfile.UserId, createdNotification);
                        }
                        catch (Exception notifEx)
                        {
                            System.Diagnostics.Debug.WriteLine($"Failed to send notification: {notifEx.Message}");
                        }
                    }

                    // Setup Response
                    return new AcceptRequestResponseDto
                    {
                        ClassId = newClass.Id, // Dùng trực tiếp, không cần createdClassId
                        PaymentRequired = !isOffline,
                        Message = isOffline ? "Nhận lớp thành công (Offline)." : "Đã chấp nhận, chờ HS thanh toán.",
                        StudentAddress = isOffline ? request.Location : null // Địa chỉ học sinh (chỉ có khi offline)
                    };
                }

                catch (Exception)
                {
                    await transaction.RollbackAsync();
                    throw;
                }
            });
    }

    #endregion

    #region Public/Shared Actions

    public async Task<ClassRequestResponseDto?> GetClassRequestByIdAsync(string id)
    {
        var request = await _uow.ClassRequests.GetAsync(
            filter: cr => cr.Id == id,
            includes: q => q.Include(cr => cr.Student).ThenInclude(s => s.User)
                            .Include(cr => cr.Tutor).ThenInclude(t => t.User)
                            .Include(cr => cr.ClassRequestSchedules) // <-- Load schedules
        );

        if (request == null) return null;

        return MapToResponseDto(request);
    }

    public async Task<(IEnumerable<ClassRequestResponseDto> Data, int TotalCount)> GetMarketplaceRequestsAsync(
        int page, int pageSize, string? status, string? subject,
        string? educationLevel, string? mode, string? locationContains)
    {
        // Parse Enums
        Enum.TryParse<ClassRequestStatus>(status, true, out var statusEnum);
        Enum.TryParse<ClassMode>(mode, true, out var modeEnum);

        // start query
        IQueryable<ClassRequest> query = _context.ClassRequests
            .Where(cr => cr.DeletedAt == null && cr.TutorId == null); // only Marketplace

        // Apply filters
        if (!string.IsNullOrEmpty(status))
            query = query.Where(cr => cr.Status == statusEnum);
        else // default to Pending
            query = query.Where(cr => cr.Status == ClassRequestStatus.Pending);

        if (!string.IsNullOrEmpty(subject))
            query = query.Where(cr => cr.Subject != null && cr.Subject.Contains(subject));

        if (!string.IsNullOrEmpty(educationLevel))
            query = query.Where(cr => cr.EducationLevel != null && cr.EducationLevel.Contains(educationLevel));

        if (!string.IsNullOrEmpty(mode))
            query = query.Where(cr => cr.Mode == modeEnum);

        if (!string.IsNullOrEmpty(locationContains))
            query = query.Where(cr => cr.Location != null && cr.Location.Contains(locationContains));

        // Count
        var totalCount = await query.CountAsync();

        // Paginate
        var pagedData = await query
            .Include(cr => cr.Student).ThenInclude(s => s.User)
            .Include(cr => cr.Tutor).ThenInclude(t => t.User)
            .Include(cr => cr.ClassRequestSchedules)
            .OrderByDescending(cr => cr.CreatedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync();

        return (pagedData.Select(cr => MapToResponseDto(cr)), totalCount);
    }

    public async Task<(IEnumerable<ClassRequestResponseDto> Data, int TotalCount)> GetMarketplaceForTutorAsync(
    string userId, int page, int pageSize, string? subject,
    string? educationLevel, string? mode, string? locationContains)
    {
        try
        {
            // Take Profile inffor
            var tutor = await _context.TutorProfiles
                .FirstOrDefaultAsync(t => t.UserId == userId);

            if (tutor == null)
            {
                Console.WriteLine($"[GetMarketplaceForTutor] Tutor not found for userId: {userId}");
                return (Enumerable.Empty<ClassRequestResponseDto>(), 0);
            }

            if (tutor.ReviewStatus != ReviewStatus.Approved)
            {
                Console.WriteLine($"[GetMarketplaceForTutor] Tutor not approved. ReviewStatus: {tutor.ReviewStatus}, userId: {userId}");
                return (Enumerable.Empty<ClassRequestResponseDto>(), 0);
            }

            // Chỉ hiển thị các request Pending (chưa được xử lý) và chưa có tutor (marketplace)
            // Filter theo TeachingSubjects và TeachingLevel của tutor để chỉ hiển thị request phù hợp
            
            // Query với điều kiện: Status = Pending
            // Lưu ý: ClassRequestStatus.Pending = 0 (enum value đầu tiên)
            // EF Core config: HasConversion<string>() - convert enum sang string khi lưu/đọc
            // Khi enum = Pending (0), EF Core convert thành string "Pending"
            // Nhưng database có thể đang lưu "0" (số) thay vì "Pending" (string)
            // Query tất cả rồi filter trong memory để handle cả hai format
            var allRequests = await _context.ClassRequests
                .Where(cr => cr.DeletedAt == null && cr.TutorId == null)
                .Include(cr => cr.Student).ThenInclude(s => s.User)
                .Include(cr => cr.ClassRequestSchedules)
                .ToListAsync();

            // Parse TeachingSubjects và TeachingLevel của tutor (lưu dưới dạng string phân tách bằng dấu phẩy)
            var tutorSubjects = tutor.TeachingSubjects?
                .Split(',', StringSplitOptions.RemoveEmptyEntries)
                .Select(s => s.Trim().ToLower())
                .Where(s => !string.IsNullOrEmpty(s))
                .ToList() ?? new List<string>();

            var tutorLevels = tutor.TeachingLevel?
                .Split(',', StringSplitOptions.RemoveEmptyEntries)
                .Select(l => l.Trim().ToLower())
                .Where(l => !string.IsNullOrEmpty(l))
                .ToList() ?? new List<string>();

            Console.WriteLine($"[GetMarketplaceForTutor] Tutor TeachingSubjects (raw): '{tutor.TeachingSubjects}'");
            Console.WriteLine($"[GetMarketplaceForTutor] Tutor subjects (parsed): {string.Join(", ", tutorSubjects)}");
            Console.WriteLine($"[GetMarketplaceForTutor] Tutor TeachingLevel (raw): '{tutor.TeachingLevel}'");
            Console.WriteLine($"[GetMarketplaceForTutor] Tutor levels (parsed): {string.Join(", ", tutorLevels)}");
            
            // Nếu tutor không có TeachingSubjects, không filter (hiển thị tất cả)
            // Nhưng nếu có thì PHẢI filter
            if (string.IsNullOrWhiteSpace(tutor.TeachingSubjects))
            {
                Console.WriteLine($"[GetMarketplaceForTutor] WARNING: Tutor has no TeachingSubjects set! Will show ALL requests.");
            }

            // Filter Pending status và match với tutor's subjects/levels
            var pendingRequests = allRequests
                .Where(cr => {
                    // Check enum value (EF Core đã convert từ database)
                    bool isPending = cr.Status == ClassRequestStatus.Pending;
                    
                    // Check raw string value từ database (nếu EF Core chưa convert đúng)
                    if (!isPending)
                    {
                        try
                        {
                            var entry = _context.Entry(cr);
                            var rawStatus = entry.Property("Status").CurrentValue?.ToString();
                            // Pending = 0, nên check cả "0" và "Pending"
                            isPending = rawStatus == "0" || rawStatus == "Pending" || rawStatus == ((int)ClassRequestStatus.Pending).ToString();
                        }
                        catch
                        {
                            return false;
                        }
                    }

                    if (!isPending) return false;

                    // Filter theo TeachingSubjects (nếu tutor có)
                    if (tutorSubjects.Any())
                    {
                        var requestSubject = cr.Subject?.Trim().ToLower();
                        if (string.IsNullOrEmpty(requestSubject))
                        {
                            Console.WriteLine($"[GetMarketplaceForTutor] Request {cr.Id} has no Subject, skipping");
                            return false; // Request không có Subject
                        }

                        // Check exact match hoặc contains match
                        bool subjectMatch = tutorSubjects.Any(ts => 
                            ts.Equals(requestSubject, StringComparison.OrdinalIgnoreCase) ||
                            requestSubject.Contains(ts, StringComparison.OrdinalIgnoreCase) ||
                            ts.Contains(requestSubject, StringComparison.OrdinalIgnoreCase)
                        );

                        if (!subjectMatch)
                        {
                            Console.WriteLine($"[GetMarketplaceForTutor] Request {cr.Id} Subject '{cr.Subject}' does not match tutor subjects '{string.Join(", ", tutorSubjects)}', skipping");
                            return false; // Không match subject
                        }

                        Console.WriteLine($"[GetMarketplaceForTutor] Request {cr.Id} Subject '{cr.Subject}' matches tutor subjects");
                    }
                    else
                    {
                        // Nếu tutor không có TeachingSubjects, hiển thị tất cả (không filter)
                        Console.WriteLine($"[GetMarketplaceForTutor] Tutor has no TeachingSubjects, showing all requests");
                    }

                    // Filter theo TeachingLevel (nếu tutor có)
                    if (tutorLevels.Any())
                    {
                        var requestLevel = cr.EducationLevel?.Trim().ToLower();
                        if (string.IsNullOrEmpty(requestLevel))
                        {
                            return false; // Request không có EducationLevel
                        }

                        // Check xem requestLevel có chứa bất kỳ tutorLevel nào không
                        // Ví dụ: "Lớp 1" match với "Tiểu học", "Lớp 7" match với "Trung học cơ sở"
                        bool levelMatch = tutorLevels.Any(tutorLevel => 
                            requestLevel.Contains(tutorLevel) || tutorLevel.Contains(requestLevel) ||
                            // Match exact: "Lớp 1", "Lớp 2", "Lớp 3", "Lớp 4", "Lớp 5" -> "Tiểu học"
                            // "Lớp 6", "Lớp 7", "Lớp 8", "Lớp 9" -> "Trung học cơ sở"
                            // "Lớp 10", "Lớp 11", "Lớp 12" -> "Trung học phổ thông"
                            (tutorLevel.Contains("tiểu học") && requestLevel.StartsWith("lớp ") && 
                             int.TryParse(requestLevel.Replace("lớp ", ""), out int grade) && grade >= 1 && grade <= 5) ||
                            (tutorLevel.Contains("trung học cơ sở") && requestLevel.StartsWith("lớp ") && 
                             int.TryParse(requestLevel.Replace("lớp ", ""), out int grade2) && grade2 >= 6 && grade2 <= 9) ||
                            (tutorLevel.Contains("trung học phổ thông") && requestLevel.StartsWith("lớp ") && 
                             int.TryParse(requestLevel.Replace("lớp ", ""), out int grade3) && grade3 >= 10 && grade3 <= 12)
                        );

                        if (!levelMatch)
                        {
                            return false; // Không match level
                        }
                    }

                    return true;
                })
                .AsQueryable();

            // TODO: Có thể thêm filter theo TeachingSubjects và TeachingLevel sau
            // Hiện tại hiển thị tất cả request Pending để tutor có thể xem và ứng tuyển

            // Filter from params
            if (!string.IsNullOrEmpty(subject))
                pendingRequests = pendingRequests.Where(cr => cr.Subject != null && cr.Subject.Contains(subject));

            if (!string.IsNullOrEmpty(educationLevel))
                pendingRequests = pendingRequests.Where(cr => cr.EducationLevel != null && cr.EducationLevel.Contains(educationLevel));

            // Paginate
            var totalCount = pendingRequests.Count();
            Console.WriteLine($"[GetMarketplaceForTutor] Found {totalCount} pending requests for tutor userId: {userId}");
            
            var pagedData = pendingRequests
                .OrderByDescending(cr => cr.CreatedAt)
                .Skip((page - 1) * pageSize)
                .Take(pageSize)
                .ToList();

            Console.WriteLine($"[GetMarketplaceForTutor] Returning {pagedData.Count} requests (page: {page}, pageSize: {pageSize})");
            return (pagedData.Select(cr => MapToResponseDto(cr)), totalCount);
        }
        catch (Exception ex)
        {
            // Exception handling - log chi tiết để debug
            Console.WriteLine($"[GetMarketplaceForTutor] ERROR: {ex.Message}");
            Console.WriteLine($"[GetMarketplaceForTutor] StackTrace: {ex.StackTrace}");
            if (ex.InnerException != null)
            {
                Console.WriteLine($"[GetMarketplaceForTutor] InnerException: {ex.InnerException.Message}");
            }
            throw new InvalidOperationException($"Lỗi Marketplace: {ex.Message}");
        }
    }
    #endregion

    #region Admin/System Actions

    public async Task<bool> UpdateClassRequestStatusAsync(string id, UpdateStatusDto dto)
    {
        try
        {
            var request = await _uow.ClassRequests.GetByIdAsync(id);
            if (request == null)
                throw new KeyNotFoundException("Không tìm thấy yêu cầu.");


            // TODO: add business rules here to restrict status changes

            request.Status = dto.Status; // enum from DTO
            await _uow.ClassRequests.UpdateAsync(request);
            await _uow.SaveChangesAsync();

            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    public async Task<int> ExpireClassRequestsAsync()
    {
        try
        {
            // find all available requests past expiry date
            var expiredRequests = await _uow.ClassRequests.GetAllAsync(
                filter: cr => cr.Status == ClassRequestStatus.Pending &&
                              cr.ExpiryDate != null &&
                              cr.ExpiryDate <= DateTimeHelper.VietnamNow);

            if (!expiredRequests.Any()) return 0;

            foreach (var request in expiredRequests)
            {
                request.Status = ClassRequestStatus.Expired;
                await _uow.ClassRequests.UpdateAsync(request);
            }

            return await _uow.SaveChangesAsync();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error expiring class requests: {ex.Message}");
            return 0;
        }
    }

    public async Task<bool> DeleteClassRequestAsync(string id)
    {
        try
        {
            var classRequest = await _uow.ClassRequests.GetByIdAsync(id);
            if (classRequest == null || classRequest.DeletedAt != null) return false;

            classRequest.DeletedAt = DateTimeHelper.VietnamNow;
            classRequest.UpdatedAt = DateTimeHelper.VietnamNow;
            await _uow.ClassRequests.UpdateAsync(classRequest);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    #endregion

    // Helper
    private static ClassRequestResponseDto MapToResponseDto(ClassRequest classRequest, bool hideLocationForTutor = false)
    {
        // Nếu là tutor xem và status là Pending (chưa thanh toán), ẩn địa chỉ học sinh
        bool shouldHideLocation = hideLocationForTutor && classRequest.Status == ClassRequestStatus.Pending;
        
        return new ClassRequestResponseDto
        {
            Id = classRequest.Id,
            Description = classRequest.Description,
            Location = shouldHideLocation ? null : classRequest.Location, // Ẩn địa chỉ nếu chưa thanh toán
            SpecialRequirements = classRequest.SpecialRequirements,
            Budget = classRequest.Budget ?? 0,
            OnlineStudyLink = classRequest.OnlineStudyLink,
            Status = classRequest.Status,
            Mode = classRequest.Mode,
            ClassStartDate = classRequest.ClassStartDate,
            ExpiryDate = classRequest.ExpiryDate,
            CreatedAt = classRequest.CreatedAt,
            StudentName = classRequest.Student?.User?.UserName,
            TutorId = classRequest.TutorId,
            TutorUserId = classRequest.Tutor?.UserId,
            TutorName = classRequest.Tutor?.User?.UserName,
            Subject = classRequest.Subject,
            EducationLevel = classRequest.EducationLevel,
            // Map list (Entity to DTO)
            Schedules = classRequest.ClassRequestSchedules.Select(s => new ClassRequestScheduleDto
            {
                DayOfWeek = s.DayOfWeek ?? 0, // byte? to byte
                StartTime = s.StartTime,
                EndTime = s.EndTime
            }).ToList()
        };
    }

    // Resolve StudentProfileId from UserID and Role
    private async Task<string> ResolveTargetStudentProfileIdAsync(string actorUserId, string userRole, string? targetStudentUserId)
    {
        // If Student: take their own profile
        if (userRole == "Student")
        {
            var profileId = await _studentProfileService.GetStudentProfileIdByUserIdAsync(actorUserId);
            if (string.IsNullOrEmpty(profileId))
                throw new KeyNotFoundException("Không tìm thấy hồ sơ học sinh của bạn.");
            return profileId;
        }

        // if Parent: need to specify StudentUserId
        if (userRole == "Parent")
        {
            if (string.IsNullOrEmpty(targetStudentUserId))
                throw new ArgumentException("Phụ huynh cần chọn học sinh (StudentUserId) để thực hiện thao tác.");

            // take target StudentProfileId based on UserID
            var childProfileId = await _studentProfileService.GetStudentProfileIdByUserIdAsync(targetStudentUserId);
            if (string.IsNullOrEmpty(childProfileId))
                throw new KeyNotFoundException("Không tìm thấy hồ sơ học sinh này.");

            // validate link Parent - Student
            var isLinked = await _parentRepo.ExistsLinkAsync(actorUserId, childProfileId);
            if (!isLinked)
                throw new UnauthorizedAccessException("Bạn không có quyền thao tác trên hồ sơ học sinh này.");

            return childProfileId;
        }

        throw new UnauthorizedAccessException("Role không hợp lệ.");
    }
    private async Task<ClassRequest> ValidateAndGetRequestAsync(string actorUserId, string userRole, string requestId)
    {
        // Request from db
        var request = await _uow.ClassRequests.GetAsync(r => r.Id == requestId);
        if (request == null)
            throw new KeyNotFoundException("Không tìm thấy yêu cầu lớp học.");

        // check authorization
        if (userRole == "Student")
        {
            // if Student: check if request.StudentId matches their profile
            var studentProfileId = await _studentProfileService.GetStudentProfileIdByUserIdAsync(actorUserId);
            if (request.StudentId != studentProfileId)
                throw new UnauthorizedAccessException("Bạn không có quyền chỉnh sửa yêu cầu này.");

        }
        else if (userRole == "Parent")
        {
            // If Parent: check if request.StudentId is linked to their children StudentProfileId
            var isLinked = await _parentRepo.ExistsLinkAsync(actorUserId, request.StudentId!);
            if (!isLinked)
                throw new UnauthorizedAccessException("Bạn không có quyền chỉnh sửa yêu cầu của học sinh này.");
        }
        else
        {
            // Other roles not allowed
            throw new UnauthorizedAccessException("Role không hợp lệ.");
        }

        return request;
    }

    /// <summary>
    ///  Check for duplicate class based on ClassRequest details
    /// </summary>
    private async Task CheckForDuplicateClassFromRequestAsync(string tutorId, ClassRequest request)
    {
        if (request.Budget == null)
            return; // No budget to compare

        // Find existing classes by the tutor with same subject, education level, mode
        var existingClasses = await _uow.Classes.GetAllAsync(
            filter: c => c.TutorId == tutorId
                       && c.Subject == request.Subject
                       && c.EducationLevel == request.EducationLevel
                       && c.Mode == request.Mode
                       && c.DeletedAt == null
                       && (c.Status == ClassStatus.Pending || c.Status == ClassStatus.Ongoing),
            includes: q => q.Include(c => c.ClassSchedules)
        );

        if (!existingClasses.Any())
            return; // There are no existing classes to compare

        // Find classes with similar price (within ±10% of request budget)
        decimal priceTolerance = request.Budget.Value * 0.1m;
        var similarPriceClasses = existingClasses
            .Where(c => c.Price.HasValue && Math.Abs(c.Price.Value - request.Budget.Value) <= priceTolerance)
            .ToList();

        if (!similarPriceClasses.Any())
            return; // There are no similar priced classes to compare

        // Check for schedule overlaps
        if (request.ClassRequestSchedules == null || !request.ClassRequestSchedules.Any())
            return;

        foreach (var existingClass in similarPriceClasses)
        {
            if (existingClass.ClassSchedules == null || !existingClass.ClassSchedules.Any())
                continue;

            // Check each schedule in the request against existing class schedules
            foreach (var newSchedule in request.ClassRequestSchedules)
            {
                foreach (var existingSchedule in existingClass.ClassSchedules)
                {
                    // Check same day of week
                    if (existingSchedule.DayOfWeek == newSchedule.DayOfWeek)
                    {
                        // Check time overlap
                        if (newSchedule.StartTime < existingSchedule.EndTime &&
                            existingSchedule.StartTime < newSchedule.EndTime)
                        {
                            throw new InvalidOperationException(
                                $"Đã tồn tại lớp học tương tự (ID: {existingClass.Id}, Tiêu đề: {existingClass.Title}) " +
                                $"với cùng môn học, cấp độ, mode và lịch học trùng lặp vào {(DayOfWeek)newSchedule.DayOfWeek} " +
                                $"từ {newSchedule.StartTime:hh\\:mm} đến {newSchedule.EndTime:hh\\:mm}. " +
                                "Vui lòng kiểm tra lại hoặc hủy lớp học trùng lặp trước khi tạo lớp mới.");
                        }
                    }
                }
            }
        }
    }

}