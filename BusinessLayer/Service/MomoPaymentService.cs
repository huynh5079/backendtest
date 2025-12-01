using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BusinessLayer.DTOs.Payment;
using BusinessLayer.Options;
using BusinessLayer.Service.Interface;
using DataLayer.Entities;
using DataLayer.Enum;
using DataLayer.Repositories.Abstraction;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BusinessLayer.Service;

public class MomoPaymentService : IMomoPaymentService
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly MomoOptions _options;
    private readonly SystemWalletOptions _systemWalletOptions;
    private readonly IUnitOfWork _uow;
    private readonly INotificationService _notificationService;
    private readonly IWalletService _walletService;
    private readonly ILogger<MomoPaymentService> _logger;
    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web);

    private const string RequestTypeCaptureWallet = "captureWallet";

    public MomoPaymentService(
        IHttpClientFactory httpClientFactory,
        IOptions<MomoOptions> momoOptions,
        IOptions<SystemWalletOptions> systemWalletOptions,
        IUnitOfWork uow,
        INotificationService notificationService,
        IWalletService walletService,
        ILogger<MomoPaymentService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _options = momoOptions.Value;
        _systemWalletOptions = systemWalletOptions.Value;
        _uow = uow;
        _notificationService = notificationService;
        _walletService = walletService;
        _logger = logger;
    }

    public async Task<CreateMomoPaymentResponseDto> CreatePaymentAsync(CreateMomoPaymentRequestDto request, string userId, CancellationToken ct = default)
    {
        if (request.Amount <= 0)
            throw new ArgumentOutOfRangeException(nameof(request.Amount), "Amount must be greater than 0.");

        // Validate context
        switch (request.ContextType)
        {
            case PaymentContextType.Escrow:
                var escrow = await _uow.Escrows.GetByIdAsync(request.ContextId, ct);
                if (escrow == null)
                    throw new ArgumentException("Escrow not found.", nameof(request.ContextId));
                break;

            case PaymentContextType.WalletDeposit:
                var user = await _uow.Users.GetByIdAsync(request.ContextId);
                if (user == null)
                    throw new ArgumentException("User not found.", nameof(request.ContextId));
                break;

            default:
                throw new ArgumentOutOfRangeException(nameof(request.ContextType));
        }

        var payment = new Payment
        {
            Provider = PaymentProvider.MoMo,
            OrderId = GenerateOrderId(),
            RequestId = Guid.NewGuid().ToString(),
            Amount = request.Amount,
            Currency = "VND",
            Status = PaymentStatus.Pending,
            ContextType = request.ContextType,
            ContextId = request.ContextType == PaymentContextType.WalletDeposit ? request.ContextId : request.ContextId,
            Message = request.Description,
            ExtraData = request.ExtraData,
        };

        await _uow.Payments.AddAsync(payment, ct);

        var momoRequest = BuildCreateRequest(payment, request.Description);
        await _uow.PaymentLogs.AddAsync(new PaymentLog
        {
            PaymentId = payment.Id,
            Event = "Create.Request",
            Payload = JsonSerializer.Serialize(momoRequest, _jsonOptions)
        }, ct);

        var httpClient = _httpClientFactory.CreateClient();
        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, _options.EndpointCreate)
        {
            Content = new StringContent(JsonSerializer.Serialize(momoRequest, _jsonOptions), Encoding.UTF8, "application/json")
        };

        using var response = await httpClient.SendAsync(httpRequest, ct);
        var responseContent = await response.Content.ReadAsStringAsync(ct);

        await _uow.PaymentLogs.AddAsync(new PaymentLog
        {
            PaymentId = payment.Id,
            Event = "Create.Response",
            Payload = responseContent
        }, ct);

        var momoResponse = JsonSerializer.Deserialize<MomoCreateResponse>(responseContent, _jsonOptions)
            ?? throw new InvalidOperationException("MoMo create payment response is invalid.");

        payment.ResultCode = momoResponse.ResultCode;
        payment.Message = momoResponse.Message;

        if (momoResponse.ResultCode != 0)
        {
            payment.Status = PaymentStatus.Failed;
            await _uow.SaveChangesAsync();
            throw new InvalidOperationException($"MoMo create payment failed: {momoResponse.Message} (code {momoResponse.ResultCode})");
        }

        await _uow.SaveChangesAsync();

        return new CreateMomoPaymentResponseDto
        {
            PaymentId = payment.Id,
            OrderId = payment.OrderId,
            PayUrl = momoResponse.PayUrl ?? momoResponse.ShortLink ?? string.Empty,
            Deeplink = momoResponse.Deeplink,
            Provider = PaymentProvider.MoMo.ToString()
        };
    }

    public async Task<MomoIpnResponseDto> HandleIpnAsync(MomoIpnRequestDto request, CancellationToken ct = default)
    {
        try
        {
            if (!ValidateIpnSignature(request))
            {
                _logger.LogWarning("MoMo IPN signature invalid for order {OrderId}", request.OrderId);
                return new MomoIpnResponseDto { ResultCode = 1, Message = "INVALID_SIGNATURE" };
            }

            var payment = await _uow.Payments.GetByOrderIdAsync(PaymentProvider.MoMo, request.OrderId, ct);
            if (payment == null)
            {
                _logger.LogWarning("MoMo IPN received for unknown order {OrderId}", request.OrderId);
                return new MomoIpnResponseDto { ResultCode = 0, Message = "ORDER_NOT_FOUND" };
            }

            // Bước 1: Log thông tin IPN request để debug
            _logger.LogInformation(
                "Nhận IPN từ MoMo: OrderId={OrderId}, ResultCode={ResultCode}, TransId={TransId}, Message={Message}",
                request.OrderId, request.ResultCode, request.TransId ?? "NULL", request.Message);

            // Bước 2: Tạo và log IPN (sẽ được lưu sau)
            var paymentLog = new PaymentLog
            {
                PaymentId = payment.Id,
                Event = "IPN",
                Payload = JsonSerializer.Serialize(request, _jsonOptions)
            };
            await _uow.PaymentLogs.AddAsync(paymentLog, ct);

            // Bước 3: Cập nhật thông tin payment cơ bản (ResultCode, Message)
            payment.ResultCode = request.ResultCode;
            payment.Message = request.Message;

            // Bước 4: Xử lý dựa trên ResultCode
            if (request.ResultCode == 0)
            {
                // Thanh toán thành công
                if (payment.Status != PaymentStatus.Paid)
                {
                    // Cập nhật trạng thái payment thành Paid
                    payment.Status = PaymentStatus.Paid;
                    payment.PaidAt = DateTime.UtcNow;
                    
                    // Cập nhật TransactionId nếu có
                    if (!string.IsNullOrWhiteSpace(request.TransId))
                    {
                        payment.TransactionId = request.TransId;
                        _logger.LogInformation(
                            "Đã cập nhật TransactionId={TransactionId} cho payment {PaymentId} (OrderId: {OrderId})",
                            request.TransId, payment.Id, payment.OrderId);
                    }
                    else
                    {
                        _logger.LogWarning(
                            "MoMo IPN thành công nhưng TransId rỗng cho payment {PaymentId} (OrderId: {OrderId})",
                            payment.Id, payment.OrderId);
                    }

                    // QUAN TRỌNG: Lưu trạng thái payment và log TRƯỚC (trước khi xử lý business logic)
                    // Đảm bảo trạng thái payment luôn được cập nhật ngay cả khi business logic thất bại
                    await _uow.SaveChangesAsync();

                    // Bước 4: Áp dụng business logic (escrow/wallet deposit) - bọc trong try-catch
                    // Nếu bước này thất bại, trạng thái payment đã được lưu là Paid (đúng)
                    try
                    {
                        _logger.LogInformation(
                            "Bắt đầu áp dụng business logic cho payment {PaymentId} (OrderId: {OrderId}, ContextType: {ContextType}, ContextId: {ContextId})",
                            payment.Id, payment.OrderId, payment.ContextType, payment.ContextId);
                        
                        await ApplyPaymentSuccessAsync(payment, request, ct);
                        
                        _logger.LogInformation(
                            "Business logic đã được áp dụng thành công cho payment {PaymentId}. Đang lưu thay đổi...",
                            payment.Id);
                        
                        // Lưu các thay đổi business logic (số dư ví, giao dịch, v.v.)
                        var savedCount = await _uow.SaveChangesAsync();
                        _logger.LogInformation(
                            "Đã lưu thành công business logic cho payment {PaymentId} (OrderId: {OrderId}). Số entities đã lưu: {SavedCount}",
                            payment.Id, payment.OrderId, savedCount);
                        
                        // Kiểm tra xem transaction đã được lưu chưa (chỉ cho WalletDeposit)
                        if (payment.ContextType == PaymentContextType.WalletDeposit)
                        {
                            try
                            {
                                var wallet = await _walletService.GetMyWalletAsync(payment.ContextId, ct);
                                var (transactions, total) = await _uow.Transactions.GetByWalletIdAsync(wallet.Id, 1, 10, ct);
                                var hasTransaction = transactions.Any(t => 
                                    t.Type == TransactionType.Credit && 
                                    t.Status == TransactionStatus.Succeeded &&
                                    t.Note != null && 
                                    t.Note.Contains(payment.OrderId));
                                
                                if (hasTransaction)
                                {
                                    _logger.LogInformation(
                                        "Đã xác nhận transaction đã được lưu vào database cho payment {PaymentId} (OrderId: {OrderId})",
                                        payment.Id, payment.OrderId);
                                }
                                else
                                {
                                    _logger.LogWarning(
                                        "CẢNH BÁO: Transaction CHƯA được tìm thấy trong database sau khi SaveChangesAsync cho payment {PaymentId} (OrderId: {OrderId})",
                                        payment.Id, payment.OrderId);
                                }
                            }
                            catch (Exception checkEx)
                            {
                                _logger.LogWarning(checkEx,
                                    "Không thể kiểm tra transaction sau khi lưu cho payment {PaymentId} (OrderId: {OrderId})",
                                    payment.Id, payment.OrderId);
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        // Ghi log lỗi chi tiết với stack trace đầy đủ
                        _logger.LogError(ex, 
                            "LỖI NGHIÊM TRỌNG: Không thể áp dụng business logic cho payment {PaymentId} (OrderId: {OrderId}, ContextType: {ContextType}, ContextId: {ContextId}). " +
                            "Trạng thái payment đã được cập nhật thành Paid nhưng tiền CHƯA được cộng vào ví. " +
                            "Lỗi: {ErrorMessage}. StackTrace: {StackTrace}",
                            payment.Id, payment.OrderId, payment.ContextType, payment.ContextId, ex.Message, ex.StackTrace);
                        
                        // Tùy chọn: Có thể tạo notification hoặc alert ở đây để admin điều tra
                    }
                }
                else
                {
                    // Payment đã được đánh dấu là Paid (IPN trùng lặp hoặc retry)
                    // QUAN TRỌNG: Kiểm tra xem business logic đã được thực thi chưa
                    // Nếu chưa (có thể do lỗi lần trước), cần retry business logic
                    
                    _logger.LogInformation(
                        "IPN retry cho payment đã Paid {PaymentId} (OrderId: {OrderId}). Kiểm tra business logic đã được thực thi chưa...",
                        payment.Id, payment.OrderId);
                    
                    // Kiểm tra xem đã có transaction cho payment này chưa
                    var hasTransaction = await CheckIfBusinessLogicAppliedAsync(payment, ct);
                    
                    if (!hasTransaction)
                    {
                        // Business logic chưa được thực thi (có thể do lỗi lần trước)
                        // Retry business logic
                        _logger.LogWarning(
                            "Payment {PaymentId} (OrderId: {OrderId}) đã Paid nhưng business logic CHƯA được thực thi. Đang retry...",
                            payment.Id, payment.OrderId);
                        
                        try
                        {
                            await ApplyPaymentSuccessAsync(payment, request, ct);
                            await _uow.SaveChangesAsync();
                            _logger.LogInformation(
                                "Đã retry thành công business logic cho payment {PaymentId} (OrderId: {OrderId})",
                                payment.Id, payment.OrderId);
                        }
                        catch (Exception retryEx)
                        {
                            _logger.LogError(retryEx,
                                "LỖI khi retry business logic cho payment {PaymentId} (OrderId: {OrderId}). " +
                                "Lỗi: {ErrorMessage}",
                                payment.Id, payment.OrderId, retryEx.Message);
                        }
                    }
                    else
                    {
                        _logger.LogInformation(
                            "Business logic đã được thực thi cho payment {PaymentId} (OrderId: {OrderId}). IPN retry bình thường.",
                            payment.Id, payment.OrderId);
                    }
                    
                    // Cập nhật TransactionId nếu chưa có hoặc nếu request có TransId mới
                    if (string.IsNullOrWhiteSpace(payment.TransactionId) && !string.IsNullOrWhiteSpace(request.TransId))
                    {
                        payment.TransactionId = request.TransId;
                        _logger.LogInformation(
                            "Đã cập nhật TransactionId={TransactionId} cho payment đã Paid {PaymentId} (OrderId: {OrderId}) - IPN retry",
                            request.TransId, payment.Id, payment.OrderId);
                    }
                    else if (!string.IsNullOrWhiteSpace(request.TransId) && payment.TransactionId != request.TransId)
                    {
                        _logger.LogWarning(
                            "IPN retry với TransId khác: Payment {PaymentId} có TransactionId={OldTransId}, IPN gửi TransId={NewTransId}",
                            payment.Id, payment.TransactionId, request.TransId);
                    }
                    
                    // Lưu log và cập nhật message/resultCode
                    await _uow.SaveChangesAsync();
                }
            }
            else
            {
                // Thanh toán thất bại (ResultCode != 0)
                // Kiểm tra xem có phải payment expired không
                var messageLower = request.Message?.ToLowerInvariant() ?? string.Empty;
                if (messageLower.Contains("hết hạn") || messageLower.Contains("expired") || 
                    messageLower.Contains("không tồn tại") || messageLower.Contains("not found"))
                {
                    payment.Status = PaymentStatus.Expired;
                    _logger.LogInformation(
                        "Payment {PaymentId} (OrderId: {OrderId}) đã hết hạn. Message: {Message}",
                        payment.Id, payment.OrderId, request.Message);
                }
                else
                {
                    payment.Status = PaymentStatus.Failed;
                }
                // Lưu trạng thái và log
                await _uow.SaveChangesAsync();
            }

            return new MomoIpnResponseDto { ResultCode = 0, Message = "OK" };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing MoMo IPN");
            return new MomoIpnResponseDto { ResultCode = 1, Message = "INTERNAL_ERROR" };
        }
    }

    public async Task<MomoQueryResponseDto> QueryPaymentAsync(string paymentId, CancellationToken ct = default)
    {
        var payment = await _uow.Payments.GetByIdAsync(paymentId);
        if (payment == null)
            throw new ArgumentException("Payment not found.", nameof(paymentId));

        var requestId = Guid.NewGuid().ToString();

        var rawData = new List<KeyValuePair<string, string>>
        {
            new("accessKey", _options.AccessKey),
            new("orderId", payment.OrderId),
            new("partnerCode", _options.PartnerCode),
            new("requestId", requestId)
        };

        var signature = Sign(rawData);

        var queryRequest = new
        {
            partnerCode = _options.PartnerCode,
            requestId,
            orderId = payment.OrderId,
            lang = "vi",
            signature
        };

        var httpClient = _httpClientFactory.CreateClient();
        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, _options.EndpointQuery)
        {
            Content = new StringContent(JsonSerializer.Serialize(queryRequest, _jsonOptions), Encoding.UTF8, "application/json")
        };

        using var response = await httpClient.SendAsync(httpRequest, ct);
        var responseContent = await response.Content.ReadAsStringAsync(ct);

        await _uow.PaymentLogs.AddAsync(new PaymentLog
        {
            PaymentId = payment.Id,
            Event = "Query.Response",
            Payload = responseContent
        }, ct);

        var momoResponse = JsonSerializer.Deserialize<MomoQueryResponse>(responseContent, _jsonOptions)
            ?? throw new InvalidOperationException("MoMo query response invalid.");

        // Cập nhật payment status nếu cần
        if (momoResponse.ResultCode != 0)
        {
            var messageLower = momoResponse.Message?.ToLowerInvariant() ?? string.Empty;
            if (messageLower.Contains("hết hạn") || messageLower.Contains("expired") || 
                messageLower.Contains("không tồn tại") || messageLower.Contains("not found"))
            {
                if (payment.Status != PaymentStatus.Expired)
                {
                    payment.Status = PaymentStatus.Expired;
                    payment.Message = momoResponse.Message;
                    await _uow.SaveChangesAsync();
                    _logger.LogInformation(
                        "Payment {PaymentId} (OrderId: {OrderId}) đã được cập nhật thành Expired từ Query. Message: {Message}",
                        payment.Id, payment.OrderId, momoResponse.Message);
                }
            }
            else if (payment.Status == PaymentStatus.Pending)
            {
                payment.Status = PaymentStatus.Failed;
                payment.Message = momoResponse.Message;
                await _uow.SaveChangesAsync();
            }
        }

        return new MomoQueryResponseDto
        {
            PaymentId = payment.Id,
            OrderId = payment.OrderId,
            ResultCode = momoResponse.ResultCode,
            Message = momoResponse.Message,
            TransId = momoResponse.TransId,
            Status = payment.Status == PaymentStatus.Expired ? "EXPIRED" : 
                     (momoResponse.ResultCode == 0 ? "SUCCESS" : "FAILED"),
            Amount = momoResponse.Amount,
            ResponseTime = momoResponse.ResponseTime
        };
    }

    public async Task<MomoRefundResponseDto> RefundPaymentAsync(string paymentId, decimal amount, string description, CancellationToken ct = default)
    {
        var payment = await _uow.Payments.GetByIdAsync(paymentId);
        if (payment == null)
            throw new ArgumentException("Payment not found.", nameof(paymentId));

        if (payment.Status != PaymentStatus.Paid)
            throw new InvalidOperationException("Only successful payments can be refunded.");

        if (string.IsNullOrWhiteSpace(payment.TransactionId))
            throw new InvalidOperationException("Payment missing MoMo transaction id.");

        var requestId = Guid.NewGuid().ToString();
        var refundId = $"REF-{DateTime.UtcNow:yyyyMMdd}-{Guid.NewGuid():N}".Substring(0, 32);

        var rawData = new List<KeyValuePair<string, string>>
        {
            new("accessKey", _options.AccessKey),
            new("amount", ((long)amount).ToString()),
            new("description", description ?? string.Empty),
            new("orderId", payment.OrderId),
            new("partnerCode", _options.PartnerCode),
            new("requestId", requestId),
            new("transId", payment.TransactionId!)
        };

        var signature = Sign(rawData);

        var refundRequest = new
        {
            partnerCode = _options.PartnerCode,
            requestId,
            orderId = payment.OrderId,
            amount = ((long)amount).ToString(),
            transId = payment.TransactionId,
            lang = "vi",
            description = description ?? string.Empty,
            signature
        };

        var httpClient = _httpClientFactory.CreateClient();
        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, _options.EndpointRefund)
        {
            Content = new StringContent(JsonSerializer.Serialize(refundRequest, _jsonOptions), Encoding.UTF8, "application/json")
        };

        using var response = await httpClient.SendAsync(httpRequest, ct);
        var responseContent = await response.Content.ReadAsStringAsync(ct);

        await _uow.PaymentLogs.AddAsync(new PaymentLog
        {
            PaymentId = payment.Id,
            Event = "Refund.Response",
            Payload = responseContent
        }, ct);

        var momoResponse = JsonSerializer.Deserialize<MomoRefundResponse>(responseContent, _jsonOptions)
            ?? throw new InvalidOperationException("MoMo refund response invalid.");

        if (momoResponse.ResultCode == 0)
        {
            payment.Status = PaymentStatus.Refunded;
            payment.ResultCode = momoResponse.ResultCode;
            payment.Message = momoResponse.Message;
            await _uow.SaveChangesAsync();
        }

        return new MomoRefundResponseDto
        {
            PaymentId = payment.Id,
            OrderId = payment.OrderId,
            RefundId = refundId,
            ResultCode = momoResponse.ResultCode,
            Message = momoResponse.Message,
            Amount = momoResponse.Amount,
            ResponseTime = momoResponse.ResponseTime
        };
    }

    #region Helpers

    private string GenerateOrderId()
    {
        var random = Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
        return $"MM-{DateTime.UtcNow:yyyyMMdd}-{random}";
    }

    private MomoCreateRequest BuildCreateRequest(Payment payment, string? description)
    {
        var amount = ((long)payment.Amount).ToString();
        var extraData = payment.ExtraData ?? string.Empty;
        var orderInfo = description ?? $"Payment for {payment.ContextType}";

        var rawData = new List<KeyValuePair<string, string>>
        {
            new("accessKey", _options.AccessKey),
            new("amount", amount),
            new("extraData", extraData),
            new("ipnUrl", _options.NotifyUrl),
            new("orderId", payment.OrderId),
            new("orderInfo", orderInfo),
            new("partnerCode", _options.PartnerCode),
            new("redirectUrl", _options.ReturnUrl),
            new("requestId", payment.RequestId),
            new("requestType", RequestTypeCaptureWallet)
        };

        var signature = Sign(rawData);

        return new MomoCreateRequest
        {
            PartnerCode = _options.PartnerCode,
            PartnerName = "TPEdu",
            StoreId = "TPEdu",
            OrderId = payment.OrderId,
            Amount = amount,
            Lang = "vi",
            OrderInfo = orderInfo,
            RequestId = payment.RequestId,
            RedirectUrl = _options.ReturnUrl,
            IpnUrl = _options.NotifyUrl,
            ExtraData = extraData,
            RequestType = RequestTypeCaptureWallet,
            Signature = signature
        };
    }

    private bool ValidateIpnSignature(MomoIpnRequestDto request)
    {
        var rawData = new List<KeyValuePair<string, string>>
        {
            new("accessKey", request.AccessKey),
            new("amount", request.Amount.ToString()),
            new("extraData", request.ExtraData ?? string.Empty),
            new("message", request.Message),
            new("orderId", request.OrderId),
            new("orderInfo", request.OrderInfo),
            new("orderType", request.OrderType),
            new("partnerCode", request.PartnerCode),
            new("payType", request.PayType),
            new("requestId", request.RequestId),
            new("responseTime", request.ResponseTime.ToString()),
            new("resultCode", request.ResultCode.ToString()),
            new("transId", request.TransId)
        };

        var signature = Sign(rawData);
        return string.Equals(signature, request.Signature, StringComparison.OrdinalIgnoreCase);
    }

    private string Sign(IEnumerable<KeyValuePair<string, string>> parameters)
    {
        var raw = string.Join("&", parameters.Select(p => $"{p.Key}={p.Value}"));
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(_options.SecretKey));
        var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(raw));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private async Task ApplyPaymentSuccessAsync(Payment payment, MomoIpnRequestDto request, CancellationToken ct)
    {
        switch (payment.ContextType)
        {
            case PaymentContextType.Escrow:
                await ApplyEscrowPaymentAsync(payment, request, ct);
                break;

            case PaymentContextType.WalletDeposit:
                await ApplyWalletDepositAsync(payment, request, ct);
                break;

            default:
                throw new ArgumentOutOfRangeException(nameof(payment.ContextType));
        }
    }

    private async Task ApplyEscrowPaymentAsync(Payment payment, MomoIpnRequestDto request, CancellationToken ct)
    {
        var escrow = await _uow.Escrows.GetByIdAsync(payment.ContextId, ct);
        if (escrow == null)
        {
            _logger.LogWarning("Escrow {EscrowId} not found when processing MoMo payment {PaymentId}", payment.ContextId, payment.Id);
            return;
        }

        if (escrow.Status == EscrowStatus.Held)
        {
            _logger.LogInformation("Escrow {EscrowId} already marked as held", escrow.Id);
            return;
        }

        var adminWallet = await _uow.Wallets.GetByUserIdAsync(_systemWalletOptions.SystemWalletUserId, ct)
            ?? await CreateWalletAsync(_systemWalletOptions.SystemWalletUserId, ct);

        adminWallet.Balance += payment.Amount;
        await _uow.Wallets.Update(adminWallet);

        await _uow.Transactions.AddAsync(new Transaction
        {
            WalletId = adminWallet.Id,
            Type = TransactionType.EscrowIn,
            Status = TransactionStatus.Succeeded,
            Amount = payment.Amount,
            Note = $"MoMo escrow payment {payment.OrderId}",
            CounterpartyUserId = escrow.StudentUserId
        }, ct);

        escrow.Status = EscrowStatus.Held;
        escrow.StudentUserId = escrow.StudentUserId ?? payment.ContextId;

        var notification = await _notificationService.CreateEscrowNotificationAsync(
            escrow.StudentUserId ?? string.Empty,
            NotificationType.EscrowPaid,
            payment.Amount,
            escrow.ClassId ?? string.Empty,
            payment.Id,
            ct);

        await _notificationService.SendRealTimeNotificationAsync(escrow.StudentUserId ?? string.Empty, notification, ct);
    }

    private async Task ApplyWalletDepositAsync(Payment payment, MomoIpnRequestDto request, CancellationToken ct)
    {
        _logger.LogInformation(
            "Bắt đầu cộng tiền vào ví cho payment {PaymentId} (OrderId: {OrderId}, UserId: {UserId}, Amount: {Amount})",
            payment.Id, payment.OrderId, payment.ContextId, payment.Amount);

        try
        {
            // Bước 1: Lấy hoặc tạo wallet
            var wallet = await _walletService.GetMyWalletAsync(payment.ContextId, ct);
            _logger.LogInformation(
                "Đã lấy wallet {WalletId} cho user {UserId}. Số dư hiện tại: {CurrentBalance}",
                wallet.Id, payment.ContextId, wallet.Balance);

            // Bước 2: Cộng tiền vào ví
            var oldBalance = wallet.Balance;
            wallet.Balance += payment.Amount;
            await _uow.Wallets.Update(wallet);
            _logger.LogInformation(
                "Đã cập nhật số dư ví: {OldBalance} -> {NewBalance} (+{Amount})",
                oldBalance, wallet.Balance, payment.Amount);

            // Bước 3: Tạo transaction record
            // Lưu TransactionId từ MoMo vào Note để hiển thị trong lịch sử
            // Ưu tiên lấy từ payment.TransactionId, nếu chưa có thì lấy từ request.TransId
            var momoTransId = !string.IsNullOrWhiteSpace(payment.TransactionId) 
                ? payment.TransactionId 
                : (!string.IsNullOrWhiteSpace(request.TransId) ? request.TransId : null);
            
            var note = !string.IsNullOrWhiteSpace(momoTransId)
                ? $"MoMo wallet deposit {payment.OrderId} (TransId: {momoTransId})"
                : $"MoMo wallet deposit {payment.OrderId}";
            
            var transaction = new Transaction
            {
                WalletId = wallet.Id,
                Type = TransactionType.Credit,
                Status = TransactionStatus.Succeeded,
                Amount = payment.Amount,
                Note = note,
                CounterpartyUserId = payment.ContextId
            };

            await _uow.Transactions.AddAsync(transaction, ct);
            _logger.LogInformation(
                "Đã tạo transaction object cho wallet {WalletId}. TransactionId={TransactionId}, WalletId={WalletId}, Amount={Amount}, Type={Type}, Note={Note}. " +
                "Transaction sẽ được lưu khi SaveChangesAsync được gọi.",
                wallet.Id, transaction.Id, wallet.Id, transaction.Amount, transaction.Type, transaction.Note);

            // Bước 4: Tạo và gửi notification (nếu lỗi ở đây không ảnh hưởng đến việc cộng tiền)
            try
            {
                var notification = await _notificationService.CreateWalletNotificationAsync(
                    payment.ContextId,
                    NotificationType.WalletDeposit,
                    payment.Amount,
                    $"Nạp ví qua MoMo (order {payment.OrderId})",
                    payment.Id,
                    ct);

                await _notificationService.SendRealTimeNotificationAsync(payment.ContextId, notification, ct);
                _logger.LogInformation("Đã gửi notification thành công cho user {UserId}", payment.ContextId);
            }
            catch (Exception notifEx)
            {
                // Notification lỗi không ảnh hưởng đến việc cộng tiền
                _logger.LogWarning(notifEx,
                    "Không thể gửi notification cho user {UserId} nhưng tiền đã được cộng vào ví",
                    payment.ContextId);
            }

            _logger.LogInformation(
                "Hoàn thành cộng tiền vào ví cho payment {PaymentId} (OrderId: {OrderId})",
                payment.Id, payment.OrderId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "LỖI khi cộng tiền vào ví cho payment {PaymentId} (OrderId: {OrderId}, UserId: {UserId}, Amount: {Amount}). " +
                "Lỗi: {ErrorMessage}. StackTrace: {StackTrace}",
                payment.Id, payment.OrderId, payment.ContextId, payment.Amount, ex.Message, ex.StackTrace);
            throw; // Re-throw để catch ở trên có thể log và xử lý
        }
    }

    private async Task<Wallet> CreateWalletAsync(string userId, CancellationToken ct)
    {
        var wallet = new Wallet { UserId = userId, Balance = 0m, Currency = "VND", IsFrozen = false };
        await _uow.Wallets.AddAsync(wallet, ct);
        await _uow.SaveChangesAsync();
        return wallet;
    }

    /// <summary>
    /// Kiểm tra xem business logic đã được thực thi cho payment chưa
    /// Bằng cách kiểm tra xem đã có transaction với note chứa OrderId chưa
    /// </summary>
    private async Task<bool> CheckIfBusinessLogicAppliedAsync(Payment payment, CancellationToken ct)
    {
        try
        {
            switch (payment.ContextType)
            {
                case PaymentContextType.WalletDeposit:
                    // Kiểm tra xem đã có transaction Credit với note chứa OrderId chưa
                    var wallet = await _walletService.GetMyWalletAsync(payment.ContextId, ct);
                    var (transactions, _) = await _uow.Transactions.GetByWalletIdAsync(wallet.Id, 1, 10, ct);
                    var hasTransaction = transactions.Any(t => 
                        t.Type == TransactionType.Credit && 
                        t.Status == TransactionStatus.Succeeded &&
                        t.Note != null && 
                        t.Note.Contains(payment.OrderId));
                    return hasTransaction;

                case PaymentContextType.Escrow:
                    // Kiểm tra xem escrow đã được đánh dấu là Held chưa
                    var escrow = await _uow.Escrows.GetByIdAsync(payment.ContextId, ct);
                    if (escrow == null) return false;
                    return escrow.Status == EscrowStatus.Held;

                default:
                    return false;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Không thể kiểm tra business logic cho payment {PaymentId} (OrderId: {OrderId}). Giả định là chưa thực thi.",
                payment.Id, payment.OrderId);
            return false; // Nếu không kiểm tra được, giả định là chưa thực thi để retry
        }
    }

    #endregion

    #region Internal DTOs

    private sealed class MomoCreateRequest
    {
        public string PartnerCode { get; set; } = default!;
        public string PartnerName { get; set; } = default!;
        public string StoreId { get; set; } = default!;
        public string RequestId { get; set; } = default!;
        public string OrderId { get; set; } = default!;
        public string Amount { get; set; } = default!;
        public string Lang { get; set; } = "vi";
        public string OrderInfo { get; set; } = default!;
        public string RedirectUrl { get; set; } = default!;
        public string IpnUrl { get; set; } = default!;
        public string ExtraData { get; set; } = string.Empty;
        public string RequestType { get; set; } = default!;
        public string Signature { get; set; } = default!;
    }

    private sealed class MomoCreateResponse
    {
        public int ResultCode { get; set; }
        public string Message { get; set; } = string.Empty;
        public string? PayUrl { get; set; }
        public string? Deeplink { get; set; }
        public string? ShortLink { get; set; }
    }

    private sealed class MomoQueryResponse
    {
        public int ResultCode { get; set; }
        public string Message { get; set; } = string.Empty;
        public string? TransId { get; set; }
        public long Amount { get; set; }
        public long ResponseTime { get; set; }
    }

    private sealed class MomoRefundResponse
    {
        public int ResultCode { get; set; }
        public string Message { get; set; } = string.Empty;
        public long Amount { get; set; }
        public long ResponseTime { get; set; }
    }

    #endregion
}

