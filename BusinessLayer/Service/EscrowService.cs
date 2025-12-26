using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using BusinessLayer.DTOs.Wallet;
using BusinessLayer.Helper;
using BusinessLayer.Options;
using BusinessLayer.Service.Interface;
using DataLayer.Entities;
using DataLayer.Enum;
using DataLayer.Repositories.Abstraction;
using Microsoft.Extensions.Options;

namespace BusinessLayer.Service
{
    public class EscrowService : IEscrowService
    {
        private readonly IUnitOfWork _uow;
        private readonly SystemWalletOptions _systemWalletOptions;
        private readonly INotificationService _notificationService;
        private readonly ICommissionService _commissionService;

        public EscrowService(
            IUnitOfWork uow,
            IOptions<SystemWalletOptions> systemWalletOptions,
            INotificationService notificationService,
            ICommissionService commissionService)
        {
            _uow = uow;
            _systemWalletOptions = systemWalletOptions.Value;
            _notificationService = notificationService;
            _commissionService = commissionService;
        }

        /// <summary>
        /// Tính tiền cọc dựa trên học phí: % học phí (không có min/max)
        /// </summary>
        private async Task<decimal> CalculateDepositAmountAsync(decimal classPrice, CancellationToken ct = default)
        {
            var settings = await _uow.SystemSettings.GetOrCreateSettingsAsync(ct);

            // Tính % học phí (ví dụ: 10% = 0.10)
            var depositAmount = Math.Round(classPrice * settings.DepositRate, 2, MidpointRounding.AwayFromZero);

            return depositAmount;
        }

        /// <summary>
        /// Kiểm tra xem lớp có đủ điều kiện để chuyển sang trạng thái "Ongoing" không
        /// Điều kiện:
        /// - Lớp ONLINE: Cần học sinh thanh toán + gia sư đặt cọc (KHÔNG CẦN đợi ngày bắt đầu)
        /// - Lớp OFFLINE: Cần đến ngày bắt đầu + có học sinh thanh toán
        /// </summary>
        private async Task<bool> CanClassStartAsync(Class classEntity, CancellationToken ct = default)
        {
            // 1. Kiểm tra có học sinh đã đóng tiền chưa
            var paidStudents = await _uow.ClassAssigns.GetAllAsync(
                filter: ca => ca.ClassId == classEntity.Id && ca.PaymentStatus == PaymentStatus.Paid);

            Console.WriteLine($"[CanClassStartAsync] Kiểm tra điều kiện cho lớp {classEntity.Id}:");
            Console.WriteLine($"  - Mode: {classEntity.Mode}");
            Console.WriteLine($"  - Số học sinh đã thanh toán (PaymentStatus = Paid): {paidStudents.Count()}");
            Console.WriteLine($"  - CurrentStudentCount trong Class: {classEntity.CurrentStudentCount}");
            
            // Debug: Log tất cả ClassAssigns
            var allClassAssigns = await _uow.ClassAssigns.GetAllAsync(
                filter: ca => ca.ClassId == classEntity.Id);
            Console.WriteLine($"[CanClassStartAsync] Tất cả ClassAssigns của lớp {classEntity.Id}:");
            foreach (var ca in allClassAssigns)
            {
                Console.WriteLine($"  - StudentId: {ca.StudentId}, PaymentStatus: {ca.PaymentStatus}, DeletedAt: {ca.DeletedAt}");
            }

            if (!paidStudents.Any())
            {
                Console.WriteLine($"[CanClassStartAsync] ❌ Chưa có học sinh nào đóng tiền");
                return false; // Chưa có học sinh nào đóng tiền
            }

            // 2. Logic khác nhau cho ONLINE và OFFLINE
            if (classEntity.Mode == ClassMode.Online)
            {
                // Lớp ONLINE: Chỉ cần học sinh thanh toán + gia sư đặt cọc (KHÔNG CẦN đợi ngày bắt đầu)
                var deposit = await _uow.TutorDepositEscrows.GetByClassIdAsync(classEntity.Id, ct);
                Console.WriteLine($"[CanClassStartAsync] Lớp ONLINE - Kiểm tra tutor deposit:");
                Console.WriteLine($"  - Deposit: {(deposit != null ? "Có" : "Không")}");
                Console.WriteLine($"  - Deposit Status: {deposit?.Status}");
                
                if (deposit == null || deposit.Status != TutorDepositStatus.Held)
                {
                    Console.WriteLine($"[CanClassStartAsync] ❌ Gia sư chưa đặt cọc");
                    return false; // Gia sư chưa đặt cọc
                }
                Console.WriteLine($"[CanClassStartAsync] ✅ Đủ điều kiện: học sinh đã thanh toán + gia sư đã đặt cọc");
                return true; // Đủ điều kiện: học sinh thanh toán + gia sư đặt cọc
            }
            else
            {
                // Lớp OFFLINE: Chỉ cần có học sinh thanh toán → chuyển sang Ongoing ngay (KHÔNG CẦN đợi ngày bắt đầu)
                Console.WriteLine($"[CanClassStartAsync] ✅ Lớp OFFLINE - Đủ điều kiện: có học sinh thanh toán");
                return true; // Đủ điều kiện: có học sinh thanh toán
            }
        }

        private async Task<Wallet> GetOrCreateWalletAsync(string userId, CancellationToken ct)
        {
            var w = await _uow.Wallets.GetByUserIdAsync(userId, ct);
            if (w != null) return w;

            w = new Wallet { UserId = userId, Balance = 0m, Currency = "VND", IsFrozen = false };
            await _uow.Wallets.AddAsync(w, ct);
            await _uow.SaveChangesAsync();
            return w;
        }

        public async Task<OperationResult> PayEscrowAsync(string actorUserId, PayEscrowRequest req, CancellationToken ct = default)
        {
            // 1) Lấy Class entity từ DB - KHÔNG TIN client
            var classEntity = await _uow.Classes.GetByIdAsync(req.ClassId);
            if (classEntity == null)
            {
                return new OperationResult { Status = "Fail", Message = "Không tìm thấy lớp học." };
            }

            // 2) Lấy ClassAssign - Mỗi học sinh = 1 Escrow riêng
            var payerUserId = string.IsNullOrWhiteSpace(req.PayerStudentUserId) ? actorUserId : req.PayerStudentUserId;
            string? studentProfileId = null;

            // Lấy studentProfileId từ payerUserId (có thể là student hoặc parent)
            if (!string.IsNullOrWhiteSpace(req.PayerStudentUserId))
            {
                var studentProfile = await _uow.StudentProfiles.GetByUserIdAsync(req.PayerStudentUserId);
                studentProfileId = studentProfile?.Id;
            }
            else
            {
                var studentProfile = await _uow.StudentProfiles.GetByUserIdAsync(payerUserId);
                studentProfileId = studentProfile?.Id;
            }

            if (string.IsNullOrWhiteSpace(studentProfileId))
            {
                return new OperationResult { Status = "Fail", Message = "Không tìm thấy hồ sơ học sinh." };
            }

            // Tìm ClassAssign
            ClassAssign? classAssign = null;
            
            // Nếu có ClassAssignId thì dùng trực tiếp (để tránh query lại và tracking conflict)
            if (!string.IsNullOrWhiteSpace(req.ClassAssignId))
            {
                classAssign = await _uow.ClassAssigns.GetByIdAsync(req.ClassAssignId);
            }
            else
            {
                // Query từ DB bằng ClassId và StudentId
                classAssign = await _uow.ClassAssigns.GetByClassAndStudentAsync(req.ClassId, studentProfileId);
            }
            
            if (classAssign == null)
            {
                return new OperationResult { Status = "Fail", Message = "Học sinh chưa ghi danh vào lớp học này." };
            }

            // Kiểm tra đã thanh toán chưa
            if (classAssign.PaymentStatus == PaymentStatus.Paid)
            {
                return new OperationResult { Status = "Fail", Message = "Học sinh đã thanh toán cho lớp học này." };
            }

            // Kiểm tra đã có Escrow chưa (tránh duplicate)
            var existingEscrows = await _uow.Escrows.GetAllAsync(
                filter: e => e.ClassAssignId == classAssign.Id && e.Status == EscrowStatus.Held);
            if (existingEscrows.Any())
            {
                return new OperationResult { Status = "Fail", Message = "Đã có escrow cho học sinh này." };
            }

            // 3) Tính GrossAmount = phần học phí của học sinh này
            // Logic: Mỗi học sinh trả TOÀN BỘ giá lớp học (Class.Price), không chia đều
            // Ví dụ: Lớp 1,100,000 VND → Mỗi học sinh trả 1,100,000 VND
            if (classEntity.Price == null || classEntity.Price <= 0)
            {
                return new OperationResult { Status = "Fail", Message = "Lớp học chưa có giá hoặc giá không hợp lệ." };
            }

            // Mỗi học sinh trả toàn bộ giá lớp học (cả lớp 1-1 và group)
            decimal grossAmount = classEntity.Price.Value;

            // 4) Tính commission rate tự động từ DB - KHÔNG TIN commissionRate từ client
            decimal commissionRate = await _commissionService.CalculateCommissionRateAsync(classEntity, null, ct);

            // 5) Lấy TutorUserId từ Class (required)
            if (string.IsNullOrWhiteSpace(classEntity.TutorId))
            {
                return new OperationResult { Status = "Fail", Message = "Lớp học chưa có gia sư." };
            }

            var tutorProfile = await _uow.TutorProfiles.GetByIdAsync(classEntity.TutorId);
            if (tutorProfile == null || string.IsNullOrWhiteSpace(tutorProfile.UserId))
            {
                return new OperationResult { Status = "Fail", Message = "Không tìm thấy thông tin gia sư." };
            }
            string tutorUserId = tutorProfile.UserId;

            // 6) Lấy ví payer và ví admin (system) – đảm bảo tồn tại
            var payerWallet = await GetOrCreateWalletAsync(payerUserId, ct);
            var adminWallet = await GetOrCreateWalletAsync(_systemWalletOptions.SystemWalletUserId, ct);

            if (payerWallet.IsFrozen) return new OperationResult { Status = "Fail", Message = "Ví bị khóa" };
            if (payerWallet.Balance < grossAmount) return new OperationResult { Status = "Fail", Message = $"Số dư không đủ. Cần {grossAmount:N0} VND, hiện có {payerWallet.Balance:N0} VND." };

            payerWallet.Balance -= grossAmount;
            adminWallet.Balance += grossAmount;

            await _uow.Wallets.Update(payerWallet);
            await _uow.Wallets.Update(adminWallet);

            await _uow.Transactions.AddAsync(new Transaction
            {
                WalletId = payerWallet.Id,
                Type = TransactionType.PayEscrow,
                Status = TransactionStatus.Succeeded,
                Amount = grossAmount * -1,
                Note = $"Pay escrow for class {req.ClassId} - student {studentProfileId}"
            }, ct);

            await _uow.Transactions.AddAsync(new Transaction
            {
                WalletId = adminWallet.Id,
                Type = TransactionType.EscrowIn,
                Status = TransactionStatus.Succeeded,
                Amount = grossAmount,
                Note = $"Receive escrow for class {req.ClassId} - student {studentProfileId}",
                CounterpartyUserId = payerUserId
            }, ct);

            // 7) Tạo Escrow per student
            var esc = new Escrow
            {
                ClassId = req.ClassId,
                ClassAssignId = classAssign.Id, // Gắn trực tiếp tới ClassAssign
                StudentUserId = payerUserId, // UserId của học sinh
                TutorUserId = tutorUserId, // Required - gia sư của lớp
                GrossAmount = grossAmount, // Phần học phí của học sinh này
                CommissionRateSnapshot = commissionRate, // Lưu snapshot commission rate
                Status = EscrowStatus.Held,
                ReleasedAmount = 0,
                RefundedAmount = 0
            };
            await _uow.Escrows.AddAsync(esc, ct);
            await _uow.SaveChangesAsync(); // Save escrow để có esc.Id

            // 8) Cập nhật ClassAssign.PaymentStatus = Paid
            classAssign.PaymentStatus = PaymentStatus.Paid;
            await _uow.ClassAssigns.UpdateAsync(classAssign);

            // Tạo notification sau khi save
            var notification = await _notificationService.CreateEscrowNotificationAsync(
                payerUserId,
                NotificationType.EscrowPaid,
                grossAmount,
                req.ClassId,
                esc.Id,
                ct);

            await _uow.SaveChangesAsync(); // Save notification và ClassAssign

            // Gửi real-time notification sau khi save
            await _notificationService.SendRealTimeNotificationAsync(payerUserId, notification, ct);

            // Gửi notification cho tất cả admin về việc nhận tiền escrow (sau khi commit transaction)
            try
            {
                var adminUsers = await _uow.Users.GetAllAsync(u => u.RoleName == "Admin");
                Console.WriteLine($"[PayEscrowAsync] Tìm thấy {adminUsers.Count()} admin users để gửi notification");
                
                foreach (var adminUser in adminUsers)
                {
                    try
                    {
                        Console.WriteLine($"[PayEscrowAsync] Đang gửi notification cho admin {adminUser.Id} ({adminUser.Email ?? adminUser.UserName})");
                        
                        // Dùng CreateAccountNotificationAsync đơn giản hơn, không cần transaction ID
                        var adminNotification = await _notificationService.CreateAccountNotificationAsync(
                            adminUser.Id,
                            NotificationType.WalletTransferIn,
                            $"Hệ thống đã nhận tiền escrow {grossAmount:N0} VND từ học sinh cho lớp học. Escrow ID: {esc.Id}",
                            req.ClassId,
                            ct);
                        
                        await _uow.SaveChangesAsync();
                        await _notificationService.SendRealTimeNotificationAsync(adminUser.Id, adminNotification, ct);
                        
                        Console.WriteLine($"[PayEscrowAsync] ✅ Đã gửi notification thành công cho admin {adminUser.Id}");
                    }
                    catch (Exception adminNotifEx)
                    {
                        Console.WriteLine($"[PayEscrowAsync] ❌ Lỗi khi gửi notification cho admin {adminUser.Id} về escrow: {adminNotifEx.Message}");
                        Console.WriteLine($"[PayEscrowAsync] Stack trace: {adminNotifEx.StackTrace}");
                    }
                }
            }
            catch (Exception notifEx)
            {
                // Log lỗi nhưng không throw để không ảnh hưởng đến flow chính
                Console.WriteLine($"[PayEscrowAsync] ❌ Lỗi khi gửi notification cho admin về escrow: {notifEx.Message}");
                Console.WriteLine($"[PayEscrowAsync] Stack trace: {notifEx.StackTrace}");
            }

            // Tính toán các giá trị để trả về
            decimal commissionAmount = Math.Round(grossAmount * commissionRate, 2, MidpointRounding.AwayFromZero);
            decimal netAmount = grossAmount - commissionAmount;

            return new OperationResult
            {
                Status = "Ok",
                Message = "Thanh toán escrow thành công",
                Data = new PayEscrowResponse
                {
                    EscrowId = esc.Id,
                    ClassId = req.ClassId,
                    ClassAssignId = classAssign.Id,
                    StudentUserId = payerUserId,
                    GrossAmount = grossAmount,
                    CommissionRateSnapshot = commissionRate,
                    CommissionAmount = commissionAmount,
                    NetAmount = netAmount,
                    Status = esc.Status.ToString()
                }
            };
        }

        // Helper: suy TutorUserId từ ClassId nếu escrow chưa có
        private async Task<string?> ResolveTutorUserIdFromClassIdAsync(string classId, CancellationToken ct)
        {
            var cls = await _uow.Classes.GetByIdAsync(classId);
            if (cls == null || string.IsNullOrWhiteSpace(cls.TutorId)) return null;

            var tutorProfile = await _uow.TutorProfiles.GetByIdAsync(cls.TutorId);
            return tutorProfile?.UserId;
        }

        public async Task<OperationResult> RefundAsync(string adminUserId, RefundEscrowRequest req, CancellationToken ct = default)
        {
            var esc = await _uow.Escrows.GetByIdAsync(req.EscrowId, ct);
            if (esc == null)
            {
                Console.WriteLine($"RefundAsync: Escrow {req.EscrowId} không tồn tại");
                return new OperationResult { Status = "Fail", Message = "Escrow không tồn tại" };
            }
            
            if (esc.Status != EscrowStatus.Held)
            {
                Console.WriteLine($"RefundAsync: Escrow {req.EscrowId} có status {esc.Status}, không thể refund (cần Held)");
                return new OperationResult { Status = "Fail", Message = $"Chỉ refund khi Held, hiện tại status: {esc.Status}" };
            }

            Console.WriteLine($"RefundAsync: Bắt đầu refund escrow {esc.Id}, số tiền: {esc.GrossAmount:N0} VND cho student {esc.StudentUserId}");
            
            // Rút từ ví hệ thống – nơi đang giữ escrow
            var adminWallet = await GetOrCreateWalletAsync(_systemWalletOptions.SystemWalletUserId, ct);
            var payerWallet = await GetOrCreateWalletAsync(esc.StudentUserId, ct);

            if (adminWallet.Balance < esc.GrossAmount)
            {
                Console.WriteLine($"RefundAsync: Số dư admin không đủ. Số dư: {adminWallet.Balance:N0}, Cần: {esc.GrossAmount:N0}");
                return new OperationResult { Status = "Fail", Message = "Số dư admin không đủ" };
            }

            adminWallet.Balance -= esc.GrossAmount;
            payerWallet.Balance += esc.GrossAmount;

            // Attach wallets để EF track changes
            await _uow.Wallets.Update(adminWallet);
            await _uow.Wallets.Update(payerWallet);

            await _uow.Transactions.AddAsync(new Transaction
            {
                WalletId = adminWallet.Id,
                Type = TransactionType.RefundOut,
                Status = TransactionStatus.Succeeded,
                Amount = -esc.GrossAmount,
                Note = $"Refund escrow {esc.Id}",
                CounterpartyUserId = esc.StudentUserId
            }, ct);

            await _uow.Transactions.AddAsync(new Transaction
            {
                WalletId = payerWallet.Id,
                Type = TransactionType.RefundIn,
                Status = TransactionStatus.Succeeded,
                Amount = esc.GrossAmount,
                Note = $"Refund received for escrow {esc.Id}",
                CounterpartyUserId = adminUserId
            }, ct);

            esc.Status = EscrowStatus.Refunded;
            esc.RefundedAt = DateTimeHelper.VietnamNow;
            esc.RefundedAmount = esc.GrossAmount; // Track đã refund bao nhiêu
            await _uow.Escrows.UpdateAsync(esc);
            await _uow.SaveChangesAsync(); // Save escrow changes

            // Tạo notification sau khi save
            var notification = await _notificationService.CreateEscrowNotificationAsync(
                esc.StudentUserId,
                NotificationType.EscrowRefunded,
                esc.GrossAmount,
                esc.ClassId,
                esc.Id,
                ct);

            await _uow.SaveChangesAsync(); // Save notification

            // Gửi real-time notification sau khi save
            await _notificationService.SendRealTimeNotificationAsync(esc.StudentUserId, notification, ct);
            Console.WriteLine($"RefundAsync: Đã hoàn tiền thành công escrow {esc.Id}, số tiền: {esc.GrossAmount:N0} VND cho student {esc.StudentUserId}");
            return new OperationResult { Status = "Ok", Message = $"Đã hoàn tiền {esc.GrossAmount:N0} VND thành công" };
        }

        public async Task<CommissionCalculationDto> CalculateCommissionAsync(string classId, decimal? grossAmount = null, CancellationToken ct = default)
        {
            var classEntity = await _uow.Classes.GetByIdAsync(classId);
            if (classEntity == null)
            {
                throw new KeyNotFoundException($"Không tìm thấy lớp học với ID '{classId}'.");
            }

            // Lấy GrossAmount từ Class.Price trong DB - KHÔNG TIN client
            // Bảo mật: Tránh client hack để giảm học phí
            decimal actualGrossAmount = grossAmount ?? classEntity.Price ?? 0;
            if (actualGrossAmount <= 0)
                throw new InvalidOperationException("Lớp học chưa có giá hoặc giá không hợp lệ.");

            var commissionType = _commissionService.DetermineCommissionType(classEntity, null);
            var commissionRate = await _commissionService.GetCommissionRateAsync(commissionType, ct);
            var commissionAmount = Math.Round(actualGrossAmount * commissionRate, 2, MidpointRounding.AwayFromZero);
            var netAmount = actualGrossAmount - commissionAmount;

            return new CommissionCalculationDto
            {
                ClassId = classId,
                ClassTitle = classEntity.Title ?? string.Empty,
                CommissionType = commissionType,
                CommissionRate = commissionRate,
                GrossAmount = actualGrossAmount, // Lấy từ DB
                CommissionAmount = commissionAmount,
                NetAmount = netAmount
            };
        }

        /// <summary>
        /// Gia sư chấp nhận lớp và đặt cọc
        /// </summary>
        public async Task<OperationResult> ProcessTutorDepositAsync(string tutorUserId, ProcessTutorDepositRequest req, CancellationToken ct = default)
        {
            // 1) Kiểm tra Class và Escrow
            var classEntity = await _uow.Classes.GetByIdAsync(req.ClassId);
            if (classEntity == null)
                return new OperationResult { Status = "Fail", Message = "Không tìm thấy lớp học." };

            // Lớp offline không cần đặt cọc, chỉ cần phí kết nối
            if (classEntity.Mode == ClassMode.Offline)
                return new OperationResult { Status = "Fail", Message = "Lớp học offline không cần đặt cọc. Chỉ cần thanh toán phí kết nối khi chấp nhận lớp." };

            // Kiểm tra có escrow nào cho lớp này chưa (có thể có nhiều escrow nếu lớp group)
            var escrows = await _uow.Escrows.GetAllAsync(
                filter: e => e.ClassId == req.ClassId && e.Status == EscrowStatus.Held);
            if (!escrows.Any())
                return new OperationResult { Status = "Fail", Message = "Không tìm thấy escrow cho lớp học này." };

            // Lấy escrow đầu tiên để tính deposit (deposit tính theo tổng học phí của lớp)
            var firstEscrow = escrows.First();

            // Kiểm tra tutor có phải là tutor của lớp không
            var tutorProfile = await _uow.TutorProfiles.GetByIdAsync(classEntity.TutorId ?? "");
            if (tutorProfile == null || tutorProfile.UserId != tutorUserId)
                return new OperationResult { Status = "Fail", Message = "Bạn không phải gia sư của lớp học này." };

            // Kiểm tra đã có deposit chưa
            var existingDeposit = await _uow.TutorDepositEscrows.GetByClassIdAsync(req.ClassId, ct);
            if (existingDeposit != null && existingDeposit.Status == TutorDepositStatus.Held)
                return new OperationResult { Status = "Fail", Message = "Đã đặt cọc cho lớp học này rồi." };

            // 2) Tính số tiền cọc = % tổng học phí của lớp
            // Với lớp group: tính deposit dựa trên tổng học phí (Class.Price), không phải từng escrow
            if (classEntity.Price == null || classEntity.Price <= 0)
                return new OperationResult { Status = "Fail", Message = "Lớp học chưa có giá." };

            var settings = await _uow.SystemSettings.GetOrCreateSettingsAsync(ct);
            decimal depositRate = settings.DepositRate; // Lưu snapshot này
            // Deposit tính theo tổng học phí của lớp (Class.Price), không phải từng escrow
            decimal depositAmount = await CalculateDepositAmountAsync(classEntity.Price.Value, ct);

            // 3) Kiểm tra số dư ví tutor
            var tutorWallet = await GetOrCreateWalletAsync(tutorUserId, ct);
            if (tutorWallet.IsFrozen)
                return new OperationResult { Status = "Fail", Message = "Ví bị khóa" };
            if (tutorWallet.Balance < depositAmount)
                return new OperationResult { Status = "Fail", Message = $"Số dư không đủ để đặt cọc. Cần {depositAmount:N0} VND, hiện có {tutorWallet.Balance:N0} VND." };

            var adminWallet = await GetOrCreateWalletAsync(_systemWalletOptions.SystemWalletUserId, ct);

            // 4) Trừ tiền từ ví tutor, cộng vào ví admin
            tutorWallet.Balance -= depositAmount;
            adminWallet.Balance += depositAmount;

            await _uow.Wallets.Update(tutorWallet);
            await _uow.Wallets.Update(adminWallet);

            // 5) Ghi transaction
            var tutorTransaction = new Transaction
            {
                WalletId = tutorWallet.Id,
                Type = TransactionType.DepositOut,
                Status = TransactionStatus.Succeeded,
                Amount = depositAmount * -1,
                Note = $"Đặt cọc cho lớp {req.ClassId}"
            };
            await _uow.Transactions.AddAsync(tutorTransaction, ct);

            await _uow.Transactions.AddAsync(new Transaction
            {
                WalletId = adminWallet.Id,
                Type = TransactionType.DepositIn,
                Status = TransactionStatus.Succeeded,
                Amount = depositAmount,
                Note = $"Nhận tiền cọc từ tutor cho lớp {req.ClassId}",
                CounterpartyUserId = tutorUserId
            }, ct);

            // 6) Tạo TutorDepositEscrow - Lưu DepositRateSnapshot để không bị ảnh hưởng khi admin đổi rate sau này
            // Deposit gắn với Class, không phải từng Escrow (với lớp group có nhiều escrow)
            var tutorDeposit = new TutorDepositEscrow
            {
                ClassId = req.ClassId,
                EscrowId = null, // Nullable - Deposit gắn với Class, không phải từng Escrow
                TutorUserId = tutorUserId,
                DepositAmount = depositAmount,
                DepositRateSnapshot = depositRate, // Lưu snapshot tỷ lệ tại thời điểm tạo
                Status = TutorDepositStatus.Held
            };
            await _uow.TutorDepositEscrows.AddAsync(tutorDeposit, ct);

            // 7) TutorUserId đã có sẵn trong Escrow khi tạo, không cần update

            // 8) Reload classEntity để có dữ liệu mới nhất (CurrentStudentCount có thể đã được cập nhật)
            classEntity = await _uow.Classes.GetByIdAsync(req.ClassId);
            if (classEntity == null)
                return new OperationResult { Status = "Fail", Message = "Không tìm thấy lớp học." };

            // 8.1) Cập nhật CurrentStudentCount dựa trên số ClassAssign thực tế có PaymentStatus = Paid
            // (đảm bảo CurrentStudentCount luôn đúng với dữ liệu thực tế)
            var actualPaidCount = await _uow.ClassAssigns.GetAllAsync(
                filter: ca => ca.ClassId == req.ClassId && 
                             ca.PaymentStatus == PaymentStatus.Paid && 
                             ca.DeletedAt == null);
            var oldCount = classEntity.CurrentStudentCount;
            classEntity.CurrentStudentCount = actualPaidCount.Count();
            if (oldCount != classEntity.CurrentStudentCount)
            {
                Console.WriteLine($"[ProcessTutorDepositAsync] Cập nhật CurrentStudentCount cho lớp {req.ClassId}: {oldCount} → {classEntity.CurrentStudentCount}");
            }

            // 9) Kiểm tra và cập nhật trạng thái lớp sang Ongoing (nếu đủ điều kiện)
            // Điều kiện để chuyển sang Ongoing:
            // - Lớp ONLINE: Cần học sinh thanh toán + gia sư đặt cọc (KHÔNG CẦN đợi ngày bắt đầu)
            // - Lớp OFFLINE: Cần đến ngày bắt đầu + có học sinh thanh toán
            var canStart = await CanClassStartAsync(classEntity, ct);
            Console.WriteLine($"[ProcessTutorDepositAsync] Kiểm tra điều kiện chuyển sang Ongoing cho lớp {req.ClassId}:");
            Console.WriteLine($"  - CanStart: {canStart}");
            Console.WriteLine($"  - Current Status: {classEntity.Status}");
            Console.WriteLine($"  - Mode: {classEntity.Mode}");
            Console.WriteLine($"  - CurrentStudentCount: {classEntity.CurrentStudentCount}");
            
            if (canStart)
            {
                var oldStatus = classEntity.Status;
                classEntity.Status = ClassStatus.Ongoing;
                await _uow.Classes.UpdateAsync(classEntity);
                Console.WriteLine($"[ProcessTutorDepositAsync] ✅ Lớp {req.ClassId} chuyển từ {oldStatus} → Ongoing (đủ điều kiện: học sinh đã thanh toán + gia sư đã đặt cọc)");
            }
            else
            {
                Console.WriteLine($"[ProcessTutorDepositAsync] ⏳ Lớp {req.ClassId} vẫn ở {classEntity.Status} (chưa đủ điều kiện)");
            }

            await _uow.SaveChangesAsync(); // Save để có transaction.Id

            // 9) Gửi notification cho TUTOR khi đặt cọc thành công
            // Dùng WalletWithdraw type vì đây là trừ tiền từ ví
            var tutorNotification = await _notificationService.CreateWalletNotificationAsync(
                tutorUserId,
                NotificationType.WalletWithdraw,
                depositAmount,
                $"Đã đặt cọc {depositAmount:N0} VND cho lớp học. Số dư ví đã được cập nhật.",
                tutorTransaction.Id,
                ct);
            await _uow.SaveChangesAsync();
            await _notificationService.SendRealTimeNotificationAsync(tutorUserId, tutorNotification, ct);

            // 10) Gửi notification cho tất cả học sinh đã thanh toán trong lớp
            var allPaidEscrows = await _uow.Escrows.GetAllAsync(
                filter: e => e.ClassId == req.ClassId && e.Status == EscrowStatus.Held);

            foreach (var paidEscrow in allPaidEscrows)
            {
                var studentNotification = await _notificationService.CreateEscrowNotificationAsync(
                    paidEscrow.StudentUserId,
                    NotificationType.EscrowPaid, // Có thể tạo notification type mới: TutorAccepted
                    depositAmount,
                    req.ClassId,
                    paidEscrow.Id,
                    ct);
                await _uow.SaveChangesAsync();
                await _notificationService.SendRealTimeNotificationAsync(paidEscrow.StudentUserId, studentNotification, ct);
            }

            return new OperationResult { Status = "Ok", Message = $"Đã đặt cọc {depositAmount:N0} VND thành công." };
        }

        /// <summary>
        /// Hoàn thành khóa học: Giải ngân học phí (bao gồm tiền cọc) + Commission
        /// Tiền cọc sẽ được cộng vào phí dạy học thay vì hoàn lại riêng
        /// </summary>
        public async Task<OperationResult> ReleaseAsync(string adminUserId, ReleaseEscrowRequest req, CancellationToken ct = default)
        {
            var esc = await _uow.Escrows.GetByIdAsync(req.EscrowId, ct);
            if (esc == null) return new OperationResult { Status = "Fail", Message = "Escrow không tồn tại" };
            if (esc.Status != EscrowStatus.Held) return new OperationResult { Status = "Fail", Message = "Escrow không ở trạng thái Held" };

            // TutorUserId đã required trong Escrow, không cần check null
            if (string.IsNullOrWhiteSpace(esc.TutorUserId))
                return new OperationResult { Status = "Fail", Message = "Escrow chưa gắn tutor" };

            // Tìm TutorDepositEscrow theo ClassId (vì deposit gắn với Class, không phải từng Escrow)
            // LƯU Ý: Deposit là tùy chọn - một số lớp có thể không có deposit (chỉ trả phí tạo lớp)
            var tutorDeposit = await _uow.TutorDepositEscrows.GetByClassIdAsync(esc.ClassId, ct);
            bool hasDeposit = tutorDeposit != null && tutorDeposit.Status == TutorDepositStatus.Held;

            var adminWallet = await GetOrCreateWalletAsync(_systemWalletOptions.SystemWalletUserId, ct);
            var tutorWallet = await GetOrCreateWalletAsync(esc.TutorUserId, ct);

            // Bước 1: Tính commission và net amount (dùng CommissionRateSnapshot)
            var commission = Math.Round(esc.GrossAmount * esc.CommissionRateSnapshot, 2, MidpointRounding.AwayFromZero);
            var net = esc.GrossAmount - commission;

            // Bước 2: Tính tổng số tiền giải ngân (net + tiền cọc nếu có)
            // Tiền cọc được cộng vào phí dạy học thay vì hoàn lại riêng
            decimal totalPayout = net;
            decimal depositAmount = 0;
            
            if (hasDeposit && tutorDeposit != null)
            {
                depositAmount = tutorDeposit.DepositAmount;
                totalPayout = net + depositAmount; // Cộng tiền cọc vào phí dạy học
            }

            // Bước 3: Kiểm tra số dư admin
            if (adminWallet.Balance < totalPayout)
                return new OperationResult { Status = "Fail", Message = $"Số dư admin không đủ để giải ngân. Cần {totalPayout:N0} VND, hiện có {adminWallet.Balance:N0} VND." };

            // Bước 4: Giải ngân tổng số tiền (net + tiền cọc) cho tutor trong 1 giao dịch
            adminWallet.Balance -= totalPayout;
            tutorWallet.Balance += totalPayout;

            await _uow.Transactions.AddAsync(new Transaction
            {
                WalletId = adminWallet.Id,
                Type = TransactionType.PayoutOut,
                Status = TransactionStatus.Succeeded,
                Amount = -totalPayout,
                Note = hasDeposit && tutorDeposit != null
                    ? $"Release payout for escrow {esc.Id} (học phí {net:N0} VND + tiền cọc {depositAmount:N0} VND)"
                    : $"Release payout for escrow {esc.Id}",
                CounterpartyUserId = esc.TutorUserId
            }, ct);

            await _uow.Transactions.AddAsync(new Transaction
            {
                WalletId = tutorWallet.Id,
                Type = TransactionType.PayoutIn,
                Status = TransactionStatus.Succeeded,
                Amount = totalPayout,
                Note = hasDeposit && tutorDeposit != null
                    ? $"Payout received for escrow {esc.Id} (học phí {net:N0} VND + tiền cọc {depositAmount:N0} VND)"
                    : $"Payout received for escrow {esc.Id}",
                CounterpartyUserId = adminUserId
            }, ct);

            // Bước 5: Cập nhật trạng thái deposit (đã được cộng vào phí dạy học)
            if (hasDeposit && tutorDeposit != null)
            {
                tutorDeposit.Status = TutorDepositStatus.Refunded; // Refunded = đã áp dụng vào phí dạy học
                tutorDeposit.RefundedAt = DateTimeHelper.VietnamNow;
                await _uow.TutorDepositEscrows.UpdateAsync(tutorDeposit);
            }

            // Bước 6: Ghi sổ commission (không đổi số dư)
            await _uow.Transactions.AddAsync(new Transaction
            {
                WalletId = adminWallet.Id,
                Type = TransactionType.Commission,
                Status = TransactionStatus.Succeeded,
                Amount = commission,
                Note = $"Commission for escrow {esc.Id}",
                CounterpartyUserId = esc.StudentUserId
            }, ct);

            // Cập nhật trạng thái escrow
            esc.Status = EscrowStatus.Released;
            esc.ReleasedAt = DateTimeHelper.VietnamNow;
            esc.ReleasedAmount = net; // Track đã release học phí (không bao gồm tiền cọc trong ReleasedAmount)

            await _uow.Wallets.Update(adminWallet);
            await _uow.Wallets.Update(tutorWallet);
            await _uow.Escrows.UpdateAsync(esc);
            await _uow.SaveChangesAsync();

            // Gửi notification cho tutor
            var notification = await _notificationService.CreateEscrowNotificationAsync(
                esc.TutorUserId,
                NotificationType.PayoutReceived,
                totalPayout,
                esc.ClassId,
                esc.Id,
                ct);
            await _uow.SaveChangesAsync();
            await _notificationService.SendRealTimeNotificationAsync(esc.TutorUserId, notification, ct);

            var message = hasDeposit && tutorDeposit != null
                ? $"Đã giải ngân {totalPayout:N0} VND (học phí {net:N0} VND + tiền cọc {depositAmount:N0} VND đã cộng vào). Commission: {commission:N0} VND."
                : $"Đã giải ngân {net:N0} VND học phí. Commission: {commission:N0} VND.";
            return new OperationResult { Status = "Ok", Message = message };
        }

        /// <summary>
        /// Xử lý vi phạm: Tịch thu tiền cọc khi tutor bỏ dở/vi phạm
        /// </summary>
        public async Task<OperationResult> ForfeitDepositAsync(string adminUserId, ForfeitDepositRequest req, CancellationToken ct = default)
        {
            var tutorDeposit = await _uow.TutorDepositEscrows.GetByIdAsync(req.TutorDepositEscrowId, ct);
            if (tutorDeposit == null)
                return new OperationResult { Status = "Fail", Message = "Không tìm thấy tiền cọc." };
            if (tutorDeposit.Status != TutorDepositStatus.Held)
                return new OperationResult { Status = "Fail", Message = "Tiền cọc không ở trạng thái Held." };

            // Lấy Class để lấy thông tin
            var classEntity = await _uow.Classes.GetByIdAsync(tutorDeposit.ClassId);
            if (classEntity == null)
                return new OperationResult { Status = "Fail", Message = "Không tìm thấy lớp học liên quan." };

            var adminWallet = await GetOrCreateWalletAsync(_systemWalletOptions.SystemWalletUserId, ct);
            var depositAmount = tutorDeposit.DepositAmount;

            if (req.RefundToStudent)
            {
                // Trả về học sinh - với lớp group, chia đều cho tất cả học sinh đã thanh toán
                var paidEscrows = await _uow.Escrows.GetAllAsync(
                    filter: e => e.ClassId == tutorDeposit.ClassId && e.Status == EscrowStatus.Held);

                if (!paidEscrows.Any())
                {
                    return new OperationResult { Status = "Fail", Message = "Không tìm thấy học sinh đã thanh toán để trả tiền." };
                }

                // Chia đều depositAmount cho tất cả học sinh đã thanh toán
                decimal refundPerStudent = Math.Round(depositAmount / paidEscrows.Count(), 2, MidpointRounding.AwayFromZero);

                if (adminWallet.Balance < depositAmount)
                    return new OperationResult { Status = "Fail", Message = "Số dư admin không đủ" };

                foreach (var esc in paidEscrows)
                {
                    var studentWallet = await GetOrCreateWalletAsync(esc.StudentUserId, ct);

                    adminWallet.Balance -= refundPerStudent;
                    studentWallet.Balance += refundPerStudent;

                    await _uow.Wallets.Update(studentWallet);

                    await _uow.Transactions.AddAsync(new Transaction
                    {
                        WalletId = adminWallet.Id,
                        Type = TransactionType.DepositForfeitOut,
                        Status = TransactionStatus.Succeeded,
                        Amount = -refundPerStudent,
                        Note = $"Tịch thu cọc và trả cho học sinh: {req.Reason}",
                        CounterpartyUserId = esc.StudentUserId
                    }, ct);

                    await _uow.Transactions.AddAsync(new Transaction
                    {
                        WalletId = studentWallet.Id,
                        Type = TransactionType.DepositForfeitIn,
                        Status = TransactionStatus.Succeeded,
                        Amount = refundPerStudent,
                        Note = $"Nhận tiền bồi thường từ cọc gia sư: {req.Reason}",
                        CounterpartyUserId = adminUserId
                    }, ct);
                }

                await _uow.Wallets.Update(adminWallet);
            }
            else
            {
                // Giữ lại cho hệ thống (phí vi phạm)
                // Không cần chuyển tiền, chỉ ghi transaction
                await _uow.Transactions.AddAsync(new Transaction
                {
                    WalletId = adminWallet.Id,
                    Type = TransactionType.Commission, // Hoặc tạo type mới: ViolationFee
                    Status = TransactionStatus.Succeeded,
                    Amount = depositAmount,
                    Note = $"Tịch thu cọc do vi phạm: {req.Reason}",
                    CounterpartyUserId = tutorDeposit.TutorUserId
                }, ct);
            }

            // Cập nhật trạng thái
            tutorDeposit.Status = TutorDepositStatus.Forfeited;
            tutorDeposit.ForfeitedAt = DateTimeHelper.VietnamNow;
            tutorDeposit.ForfeitReason = req.Reason;

            await _uow.TutorDepositEscrows.UpdateAsync(tutorDeposit);
            await _uow.SaveChangesAsync();

            // Gửi notification cho tutor khi deposit bị forfeited
            if (!string.IsNullOrEmpty(tutorDeposit.TutorUserId))
            {
                try
                {
                    var notification = await _notificationService.CreateWalletNotificationAsync(
                        tutorDeposit.TutorUserId,
                        NotificationType.TutorDepositForfeited,
                        depositAmount,
                        $"Tiền cọc đã bị tịch thu: {req.Reason}",
                        tutorDeposit.Id,
                        ct);
                    await _uow.SaveChangesAsync();
                    await _notificationService.SendRealTimeNotificationAsync(tutorDeposit.TutorUserId, notification, ct);
                }
                catch (Exception notifEx)
                {
                    System.Diagnostics.Debug.WriteLine($"Failed to send notification: {notifEx.Message}");
                }
            }

            return new OperationResult { Status = "Ok", Message = $"Đã tịch thu tiền cọc {depositAmount:N0} VND." };
        }

        /// <summary>
        /// Partial release escrow - Release một phần cho tutor (khi đã dạy một phần)
        /// </summary>
        public async Task<OperationResult> PartialReleaseAsync(string adminUserId, PartialReleaseEscrowRequest req, CancellationToken ct = default)
        {
            var esc = await _uow.Escrows.GetByIdAsync(req.EscrowId, ct);
            if (esc == null) return new OperationResult { Status = "Fail", Message = "Escrow không tồn tại" };
            if (esc.Status != EscrowStatus.Held && esc.Status != EscrowStatus.PartiallyReleased)
                return new OperationResult { Status = "Fail", Message = "Escrow không ở trạng thái hợp lệ để partial release" };

            if (req.ReleasePercentage <= 0 || req.ReleasePercentage > 1)
                return new OperationResult { Status = "Fail", Message = "ReleasePercentage phải trong khoảng (0, 1]" };

            var adminWallet = await GetOrCreateWalletAsync(_systemWalletOptions.SystemWalletUserId, ct);
            var tutorWallet = await GetOrCreateWalletAsync(esc.TutorUserId, ct);

            // Tính số tiền còn lại có thể release
            decimal remainingAmount = esc.GrossAmount - esc.ReleasedAmount;
            decimal releaseAmount = Math.Round(remainingAmount * req.ReleasePercentage, 2, MidpointRounding.AwayFromZero);

            if (releaseAmount <= 0)
                return new OperationResult { Status = "Fail", Message = "Không còn tiền để release" };

            // Tính commission và net
            var commission = Math.Round(releaseAmount * esc.CommissionRateSnapshot, 2, MidpointRounding.AwayFromZero);
            var net = releaseAmount - commission;

            if (adminWallet.Balance < net)
                return new OperationResult { Status = "Fail", Message = "Số dư admin không đủ để giải ngân" };

            adminWallet.Balance -= net;
            tutorWallet.Balance += net;

            await _uow.Transactions.AddAsync(new Transaction
            {
                WalletId = adminWallet.Id,
                Type = TransactionType.PayoutOut,
                Status = TransactionStatus.Succeeded,
                Amount = -net,
                Note = $"Partial release {req.ReleasePercentage:P0} for escrow {esc.Id}",
                CounterpartyUserId = esc.TutorUserId
            }, ct);

            await _uow.Transactions.AddAsync(new Transaction
            {
                WalletId = tutorWallet.Id,
                Type = TransactionType.PayoutIn,
                Status = TransactionStatus.Succeeded,
                Amount = net,
                Note = $"Partial payout received for escrow {esc.Id}",
                CounterpartyUserId = adminUserId
            }, ct);

            // Cập nhật escrow
            esc.ReleasedAmount += releaseAmount;
            if (esc.ReleasedAmount >= esc.GrossAmount)
            {
                esc.Status = EscrowStatus.Released;
            }
            else
            {
                esc.Status = EscrowStatus.PartiallyReleased;
            }
            await _uow.Escrows.UpdateAsync(esc);
            await _uow.SaveChangesAsync();

            // Ghi commission
            await _uow.Transactions.AddAsync(new Transaction
            {
                WalletId = adminWallet.Id,
                Type = TransactionType.Commission,
                Status = TransactionStatus.Succeeded,
                Amount = commission,
                Note = $"Commission from partial release escrow {esc.Id}",
                CounterpartyUserId = esc.TutorUserId
            }, ct);

            // Notification cho tutor
            var notification = await _notificationService.CreateEscrowNotificationAsync(
                esc.TutorUserId,
                NotificationType.EscrowReleased,
                net,
                esc.ClassId,
                esc.Id,
                ct);
            await _notificationService.SendRealTimeNotificationAsync(esc.TutorUserId, notification, ct);

            return new OperationResult
            {
                Status = "Ok",
                Message = $"Đã giải ngân {releaseAmount:N0} VND ({req.ReleasePercentage:P0}) cho tutor."
            };
        }

        /// <summary>
        /// Partial refund escrow - Refund một phần cho student (phần chưa học)
        /// </summary>
        public async Task<OperationResult> PartialRefundAsync(string adminUserId, PartialRefundEscrowRequest req, CancellationToken ct = default)
        {
            var esc = await _uow.Escrows.GetByIdAsync(req.EscrowId, ct);
            if (esc == null) return new OperationResult { Status = "Fail", Message = "Escrow không tồn tại" };
            if (esc.Status != EscrowStatus.Held && esc.Status != EscrowStatus.PartiallyReleased)
                return new OperationResult { Status = "Fail", Message = "Escrow không ở trạng thái hợp lệ để partial refund" };

            if (req.RefundPercentage <= 0 || req.RefundPercentage > 1)
                return new OperationResult { Status = "Fail", Message = "RefundPercentage phải trong khoảng (0, 1]" };

            var adminWallet = await GetOrCreateWalletAsync(_systemWalletOptions.SystemWalletUserId, ct);
            var studentWallet = await GetOrCreateWalletAsync(esc.StudentUserId, ct);

            // Tính số tiền còn lại có thể refund (chưa release)
            decimal remainingAmount = esc.GrossAmount - esc.ReleasedAmount - esc.RefundedAmount;
            decimal refundAmount = Math.Round(remainingAmount * req.RefundPercentage, 2, MidpointRounding.AwayFromZero);

            if (refundAmount <= 0)
                return new OperationResult { Status = "Fail", Message = "Không còn tiền để refund" };

            if (adminWallet.Balance < refundAmount)
                return new OperationResult { Status = "Fail", Message = "Số dư admin không đủ" };

            adminWallet.Balance -= refundAmount;
            studentWallet.Balance += refundAmount;

            await _uow.Transactions.AddAsync(new Transaction
            {
                WalletId = adminWallet.Id,
                Type = TransactionType.RefundOut,
                Status = TransactionStatus.Succeeded,
                Amount = -refundAmount,
                Note = $"Partial refund {req.RefundPercentage:P0} for escrow {esc.Id}",
                CounterpartyUserId = esc.StudentUserId
            }, ct);

            await _uow.Transactions.AddAsync(new Transaction
            {
                WalletId = studentWallet.Id,
                Type = TransactionType.RefundIn,
                Status = TransactionStatus.Succeeded,
                Amount = refundAmount,
                Note = $"Partial refund received for escrow {esc.Id}",
                CounterpartyUserId = adminUserId
            }, ct);

            // Cập nhật escrow
            esc.RefundedAmount += refundAmount;
            if (esc.RefundedAmount + esc.ReleasedAmount >= esc.GrossAmount)
            {
                esc.Status = EscrowStatus.Refunded;
                esc.RefundedAt = DateTimeHelper.VietnamNow;
            }
            await _uow.Escrows.UpdateAsync(esc);
            await _uow.SaveChangesAsync();

            // Notification cho student
            var notification = await _notificationService.CreateEscrowNotificationAsync(
                esc.StudentUserId,
                NotificationType.EscrowRefunded,
                refundAmount,
                esc.ClassId,
                esc.Id,
                ct);
            await _notificationService.SendRealTimeNotificationAsync(esc.StudentUserId, notification, ct);

            return new OperationResult
            {
                Status = "Ok",
                Message = $"Đã hoàn {refundAmount:N0} VND ({req.RefundPercentage:P0}) cho học sinh."
            };
        }
    }
}


