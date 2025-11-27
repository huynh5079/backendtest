using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using BusinessLayer.DTOs.Wallet;
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
            using var tx = await _uow.BeginTransactionAsync();

            // 1) Lấy Class entity từ DB - KHÔNG TIN client
            var classEntity = await _uow.Classes.GetByIdAsync(req.ClassId);
            if (classEntity == null)
            {
                return new OperationResult { Status = "Fail", Message = "Không tìm thấy lớp học." };
            }

            // 2) Lấy GrossAmount từ Class.Price trong DB - KHÔNG TIN grossAmount từ client
            // Bảo mật: Tránh client hack để giảm học phí
            if (classEntity.Price == null || classEntity.Price <= 0)
            {
                return new OperationResult { Status = "Fail", Message = "Lớp học chưa có giá hoặc giá không hợp lệ." };
            }
            decimal grossAmount = classEntity.Price.Value; // Lấy từ DB, ignore req.GrossAmount

            // 3) Tính commission rate tự động từ DB - KHÔNG TIN commissionRate từ client
            // Bảo mật: Tránh client hack để giảm commission
            // Chỉ admin mới có thể set commissionRate (qua admin panel), client không được phép
            decimal commissionRate = await _commissionService.CalculateCommissionRateAsync(classEntity, null, ct);

            var payerUserId = string.IsNullOrWhiteSpace(req.PayerStudentUserId) ? actorUserId : req.PayerStudentUserId;
            // Lấy ví payer và ví admin (system) – đảm bảo tồn tại
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
                Note = $"Pay escrow for class {req.ClassId}"
            }, ct);

            await _uow.Transactions.AddAsync(new Transaction
            {
                WalletId = adminWallet.Id,
                Type = TransactionType.EscrowIn,
                Status = TransactionStatus.Succeeded,
                Amount = grossAmount,
                Note = $"Receive escrow for class {req.ClassId}",
                CounterpartyUserId = payerUserId
            }, ct);

            var esc = new Escrow
            {
                ClassId = req.ClassId,
                PayerUserId = payerUserId,
                TutorUserId = null, // Chưa có tutor - sẽ set khi tutor chấp nhận và đặt cọc
                GrossAmount = grossAmount, // Lấy từ DB, không tin client
                CommissionRate = commissionRate, // Tính tự động từ DB, không tin client
                Status = EscrowStatus.Held
            };
            await _uow.Escrows.AddAsync(esc, ct);
            await _uow.SaveChangesAsync(); // Save escrow để có esc.Id

            // Tạo notification sau khi save
            var notification = await _notificationService.CreateEscrowNotificationAsync(
                payerUserId, 
                NotificationType.EscrowPaid, 
                grossAmount, 
                req.ClassId, 
                esc.Id, 
                ct);
            
            await _uow.SaveChangesAsync(); // Save notification
            
            // Gửi real-time notification sau khi save
            await _notificationService.SendRealTimeNotificationAsync(payerUserId, notification, ct);
            
            await tx.CommitAsync();
            return new OperationResult { Status = "Ok" };
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
            if (esc == null) return new OperationResult { Status = "Fail", Message = "Escrow không tồn tại" };
            if (esc.Status != EscrowStatus.Held) return new OperationResult { Status = "Fail", Message = "Chỉ refund khi Held" };

            using var tx = await _uow.BeginTransactionAsync();

            // Rút từ ví hệ thống – nơi đang giữ escrow
            var adminWallet = await GetOrCreateWalletAsync(_systemWalletOptions.SystemWalletUserId, ct);
            var payerWallet = await GetOrCreateWalletAsync(esc.PayerUserId, ct);

            if (adminWallet.Balance < esc.GrossAmount) return new OperationResult { Status = "Fail", Message = "Số dư admin không đủ" };

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
                CounterpartyUserId = esc.PayerUserId
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
            esc.RefundedAt = DateTime.UtcNow;
            await _uow.SaveChangesAsync(); // Save escrow changes

            // Tạo notification sau khi save
            var notification = await _notificationService.CreateEscrowNotificationAsync(
                esc.PayerUserId, 
                NotificationType.EscrowRefunded, 
                esc.GrossAmount, 
                esc.ClassId, 
                esc.Id, 
                ct);
            
            await _uow.SaveChangesAsync(); // Save notification
            
            // Gửi real-time notification sau khi save
            await _notificationService.SendRealTimeNotificationAsync(esc.PayerUserId, notification, ct);
            
            await tx.CommitAsync();
            return new OperationResult { Status = "Ok" };
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

            // Tìm Escrow của lớp này
            var escrows = await _uow.Escrows.GetAllAsync(
                filter: e => e.ClassId == req.ClassId && e.Status == EscrowStatus.Held);
            var esc = escrows.FirstOrDefault();
            if (esc == null)
                return new OperationResult { Status = "Fail", Message = "Không tìm thấy escrow cho lớp học này." };

            // Kiểm tra tutor có phải là tutor của lớp không
            var tutorProfile = await _uow.TutorProfiles.GetByIdAsync(classEntity.TutorId ?? "");
            if (tutorProfile == null || tutorProfile.UserId != tutorUserId)
                return new OperationResult { Status = "Fail", Message = "Bạn không phải gia sư của lớp học này." };

            // Kiểm tra đã có deposit chưa
            var existingDeposit = await _uow.TutorDepositEscrows.GetByClassIdAsync(req.ClassId, ct);
            if (existingDeposit != null && existingDeposit.Status == TutorDepositStatus.Held)
                return new OperationResult { Status = "Fail", Message = "Đã đặt cọc cho lớp học này rồi." };

            using var tx = await _uow.BeginTransactionAsync();

            // 2) Tính số tiền cọc = % học phí
            // Lấy học phí từ Escrow và lấy DepositRate từ SystemSettings
            var settings = await _uow.SystemSettings.GetOrCreateSettingsAsync(ct);
            decimal depositRate = settings.DepositRate; // Lưu snapshot này
            decimal depositAmount = await CalculateDepositAmountAsync(esc.GrossAmount, ct);

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
            await _uow.Transactions.AddAsync(new Transaction
            {
                WalletId = tutorWallet.Id,
                Type = TransactionType.DepositOut,
                Status = TransactionStatus.Succeeded,
                Amount = depositAmount * -1,
                Note = $"Đặt cọc cho lớp {req.ClassId}"
            }, ct);

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
            var tutorDeposit = new TutorDepositEscrow
            {
                ClassId = req.ClassId,
                EscrowId = esc.Id,
                TutorUserId = tutorUserId,
                DepositAmount = depositAmount,
                DepositRateSnapshot = depositRate, // Lưu snapshot tỷ lệ tại thời điểm tạo
                Status = TutorDepositStatus.Held
            };
            await _uow.TutorDepositEscrows.AddAsync(tutorDeposit, ct);

            // 7) Cập nhật Escrow với TutorUserId
            esc.TutorUserId = tutorUserId;
            await _uow.Escrows.UpdateAsync(esc);

            // 8) Cập nhật trạng thái lớp (nếu cần)
            if (classEntity.Status == ClassStatus.Pending)
            {
                classEntity.Status = ClassStatus.Ongoing;
                await _uow.Classes.UpdateAsync(classEntity);
            }

            await _uow.SaveChangesAsync();
            await tx.CommitAsync();

            // 9) Gửi notification cho học sinh
            var studentNotification = await _notificationService.CreateEscrowNotificationAsync(
                esc.PayerUserId,
                NotificationType.EscrowPaid, // Có thể tạo notification type mới: TutorAccepted
                depositAmount,
                req.ClassId,
                esc.Id,
                ct);
            await _uow.SaveChangesAsync();
            await _notificationService.SendRealTimeNotificationAsync(esc.PayerUserId, studentNotification, ct);

            return new OperationResult { Status = "Ok", Message = $"Đã đặt cọc {depositAmount:N0} VND thành công." };
        }

        /// <summary>
        /// Hoàn thành khóa học: Hoàn cọc + Giải ngân học phí + Commission
        /// </summary>
        public async Task<OperationResult> ReleaseAsync(string adminUserId, ReleaseEscrowRequest req, CancellationToken ct = default)
        {
            var esc = await _uow.Escrows.GetByIdAsync(req.EscrowId, ct);
            if (esc == null) return new OperationResult { Status = "Fail", Message = "Escrow không tồn tại" };
            if (esc.Status != EscrowStatus.Held) return new OperationResult { Status = "Fail", Message = "Escrow không ở trạng thái Held" };

            using var tx = await _uow.BeginTransactionAsync();

            // Đảm bảo có TutorUserId
            var tutorUserId = string.IsNullOrWhiteSpace(esc.TutorUserId)
                ? await ResolveTutorUserIdFromClassIdAsync(esc.ClassId, ct)
                : esc.TutorUserId;

            if (string.IsNullOrWhiteSpace(tutorUserId))
                return new OperationResult { Status = "Fail", Message = "Escrow chưa gắn tutor" };

            esc.TutorUserId = tutorUserId;

            // Tìm TutorDepositEscrow
            var tutorDeposit = await _uow.TutorDepositEscrows.GetByEscrowIdAsync(esc.Id, ct);
            if (tutorDeposit == null || tutorDeposit.Status != TutorDepositStatus.Held)
                return new OperationResult { Status = "Fail", Message = "Không tìm thấy tiền cọc hoặc đã được xử lý." };

            var adminWallet = await GetOrCreateWalletAsync(_systemWalletOptions.SystemWalletUserId, ct);
            var tutorWallet = await GetOrCreateWalletAsync(tutorUserId, ct);

            // Bước 1: Tính commission và net amount
            var commission = Math.Round(esc.GrossAmount * esc.CommissionRate, 2, MidpointRounding.AwayFromZero);
            var net = esc.GrossAmount - commission;

            // Bước 2: Giải ngân học phí (net) cho tutor
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
                Note = $"Release payout for escrow {esc.Id}",
                CounterpartyUserId = esc.TutorUserId
            }, ct);

            await _uow.Transactions.AddAsync(new Transaction
            {
                WalletId = tutorWallet.Id,
                Type = TransactionType.PayoutIn,
                Status = TransactionStatus.Succeeded,
                Amount = net,
                Note = $"Payout received for escrow {esc.Id}",
                CounterpartyUserId = adminUserId
            }, ct);

            // Bước 3: Hoàn cọc cho tutor
            var depositAmount = tutorDeposit.DepositAmount;
            if (adminWallet.Balance < depositAmount)
                return new OperationResult { Status = "Fail", Message = "Số dư admin không đủ để hoàn cọc" };

            adminWallet.Balance -= depositAmount;
            tutorWallet.Balance += depositAmount;

            await _uow.Transactions.AddAsync(new Transaction
            {
                WalletId = adminWallet.Id,
                Type = TransactionType.DepositRefundOut,
                Status = TransactionStatus.Succeeded,
                Amount = -depositAmount,
                Note = $"Hoàn cọc cho tutor từ escrow {esc.Id}",
                CounterpartyUserId = tutorUserId
            }, ct);

            await _uow.Transactions.AddAsync(new Transaction
            {
                WalletId = tutorWallet.Id,
                Type = TransactionType.DepositRefundIn,
                Status = TransactionStatus.Succeeded,
                Amount = depositAmount,
                Note = $"Nhận hoàn cọc từ escrow {esc.Id}",
                CounterpartyUserId = adminUserId
            }, ct);

            // Bước 4: Ghi sổ commission (không đổi số dư)
            await _uow.Transactions.AddAsync(new Transaction
            {
                WalletId = adminWallet.Id,
                Type = TransactionType.Commission,
                Status = TransactionStatus.Succeeded,
                Amount = commission,
                Note = $"Commission for escrow {esc.Id}",
                CounterpartyUserId = esc.PayerUserId
            }, ct);

            // Cập nhật trạng thái
            esc.Status = EscrowStatus.Released;
            esc.ReleasedAt = DateTime.UtcNow;
            tutorDeposit.Status = TutorDepositStatus.Refunded;
            tutorDeposit.RefundedAt = DateTime.UtcNow;

            await _uow.Wallets.Update(adminWallet);
            await _uow.Wallets.Update(tutorWallet);
            await _uow.Escrows.UpdateAsync(esc);
            await _uow.TutorDepositEscrows.UpdateAsync(tutorDeposit);
            await _uow.SaveChangesAsync();

            // Gửi notification cho tutor
            var totalReceived = net + depositAmount;
            var notification = await _notificationService.CreateEscrowNotificationAsync(
                tutorUserId,
                NotificationType.PayoutReceived,
                totalReceived,
                esc.ClassId,
                esc.Id,
                ct);
            await _uow.SaveChangesAsync();
            await _notificationService.SendRealTimeNotificationAsync(tutorUserId, notification, ct);

            await tx.CommitAsync();
            return new OperationResult { Status = "Ok", Message = $"Đã giải ngân {net:N0} VND học phí + hoàn {depositAmount:N0} VND cọc. Commission: {commission:N0} VND." };
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

            var esc = await _uow.Escrows.GetByIdAsync(tutorDeposit.EscrowId, ct);
            if (esc == null)
                return new OperationResult { Status = "Fail", Message = "Không tìm thấy escrow liên quan." };

            using var tx = await _uow.BeginTransactionAsync();

            var adminWallet = await GetOrCreateWalletAsync(_systemWalletOptions.SystemWalletUserId, ct);
            var depositAmount = tutorDeposit.DepositAmount;

            if (req.RefundToStudent)
            {
                // Trả về học sinh
                var studentWallet = await GetOrCreateWalletAsync(esc.PayerUserId, ct);
                if (adminWallet.Balance < depositAmount)
                    return new OperationResult { Status = "Fail", Message = "Số dư admin không đủ" };

                adminWallet.Balance -= depositAmount;
                studentWallet.Balance += depositAmount;

                await _uow.Wallets.Update(adminWallet);
                await _uow.Wallets.Update(studentWallet);

                await _uow.Transactions.AddAsync(new Transaction
                {
                    WalletId = adminWallet.Id,
                    Type = TransactionType.DepositForfeitOut,
                    Status = TransactionStatus.Succeeded,
                    Amount = -depositAmount,
                    Note = $"Tịch thu cọc và trả cho học sinh: {req.Reason}",
                    CounterpartyUserId = esc.PayerUserId
                }, ct);

                await _uow.Transactions.AddAsync(new Transaction
                {
                    WalletId = studentWallet.Id,
                    Type = TransactionType.DepositForfeitIn,
                    Status = TransactionStatus.Succeeded,
                    Amount = depositAmount,
                    Note = $"Nhận tiền bồi thường từ cọc gia sư: {req.Reason}",
                    CounterpartyUserId = adminUserId
                }, ct);
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
            tutorDeposit.ForfeitedAt = DateTime.UtcNow;
            tutorDeposit.ForfeitReason = req.Reason;

            await _uow.TutorDepositEscrows.UpdateAsync(tutorDeposit);
            await _uow.SaveChangesAsync();
            await tx.CommitAsync();

            return new OperationResult { Status = "Ok", Message = $"Đã tịch thu tiền cọc {depositAmount:N0} VND." };
        }
    }
}


