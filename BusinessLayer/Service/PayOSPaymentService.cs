using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using BusinessLayer.DTOs.Payment;
using BusinessLayer.DTOs.Wallet;
using BusinessLayer.Helper;
using BusinessLayer.Options;
using BusinessLayer.Service.Interface;
using DataLayer.Entities;
using DataLayer.Enum;
using DataLayer.Repositories.Abstraction;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BusinessLayer.Service;

public class PayOSPaymentService : IPayOSPaymentService
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly PayOSOptions _options;
    private readonly SystemWalletOptions _systemWalletOptions;
    private readonly IUnitOfWork _uow;
    private readonly INotificationService _notificationService;
    private readonly IWalletService _walletService;
    private readonly IEmailService _emailService;
    private readonly ILogger<PayOSPaymentService> _logger;
    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false // Compact JSON for PayOS signature
    };

    public PayOSPaymentService(
        IHttpClientFactory httpClientFactory,
        IOptions<PayOSOptions> payOSOptions,
        IOptions<SystemWalletOptions> systemWalletOptions,
        IUnitOfWork uow,
        INotificationService notificationService,
        IWalletService walletService,
        IEmailService emailService,
        ILogger<PayOSPaymentService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _options = payOSOptions.Value;
        _systemWalletOptions = systemWalletOptions.Value;
        _uow = uow;
        
        // Log PayOS configuration on service initialization
        Console.WriteLine($"[PayOSPaymentService] ⚙️ PayOS Configuration loaded:");
        Console.WriteLine($"[PayOSPaymentService] ⚙️   ClientId = {_options.ClientId ?? "NULL"}");
        Console.WriteLine($"[PayOSPaymentService] ⚙️   ApiKey = {(_options.ApiKey?.Length > 0 ? "***" + _options.ApiKey.Substring(Math.Max(0, _options.ApiKey.Length - 4)) : "NULL")}");
        Console.WriteLine($"[PayOSPaymentService] ⚙️   ChecksumKey = {(_options.ChecksumKey?.Length > 0 ? "***" + _options.ChecksumKey.Substring(Math.Max(0, _options.ChecksumKey.Length - 4)) : "NULL")} (length: {_options.ChecksumKey?.Length ?? 0})");
        Console.WriteLine($"[PayOSPaymentService] ⚙️   EndpointCreate = {_options.EndpointCreate ?? "NULL"}");
        Console.WriteLine($"[PayOSPaymentService] ⚙️   ReturnUrl = {_options.ReturnUrl ?? "NULL"}");
        Console.WriteLine($"[PayOSPaymentService] ⚙️   CancelUrl = {_options.CancelUrl ?? "NULL"}");
        
        // Validate configuration
        ValidatePayOSConfiguration();
        _notificationService = notificationService;
        _walletService = walletService;
        _emailService = emailService;
        _logger = logger;
        
        // Log configuration on startup (without sensitive data)
        _logger.LogInformation(
            "PayOSPaymentService initialized. EndpointCreate: {Endpoint}, ReturnUrl: {ReturnUrl}, CancelUrl: {CancelUrl}",
            _options.EndpointCreate, _options.ReturnUrl, _options.CancelUrl);
    }

    public async Task<CreatePayOSPaymentResponseDto> CreatePaymentAsync(CreatePayOSPaymentRequestDto request, string userId, CancellationToken ct = default)
    {
        try
        {
            // Validate PayOS configuration
            ValidatePayOSConfiguration();

            if (request.Amount <= 0)
                throw new ArgumentOutOfRangeException(nameof(request.Amount), "Amount must be greater than 0.");

            // Determine ContextId based on ContextType
            string contextId;
            switch (request.ContextType)
            {
                case PaymentContextType.Escrow:
                    if (string.IsNullOrWhiteSpace(request.ContextId))
                        throw new ArgumentException("ContextId is required for Escrow payment.", nameof(request.ContextId));

                    var escrow = await _uow.Escrows.GetByIdAsync(request.ContextId, ct);
                    if (escrow == null)
                        throw new ArgumentException("Escrow not found.", nameof(request.ContextId));

                    contextId = request.ContextId;
                    break;

                case PaymentContextType.WalletDeposit:
                    if (string.IsNullOrWhiteSpace(request.ContextId))
                    {
                        var authenticatedUser = await _uow.Users.GetByIdAsync(userId);
                        if (authenticatedUser == null)
                            throw new ArgumentException("Authenticated user not found.", nameof(userId));

                        contextId = userId;
                        _logger.LogInformation(
                            "WalletDeposit payment: ContextId not provided, using userId from authentication: {UserId}",
                            userId);
                    }
                    else
                    {
                        if (request.ContextId != userId)
                        {
                            throw new UnauthorizedAccessException(
                                $"You can only create WalletDeposit payment for your own account. " +
                                $"Provided ContextId: {request.ContextId}, Your UserId: {userId}");
                        }

                        var user = await _uow.Users.GetByIdAsync(request.ContextId);
                        if (user == null)
                            throw new ArgumentException("User not found.", nameof(request.ContextId));

                        contextId = request.ContextId;
                    }
                    break;

                default:
                    throw new ArgumentOutOfRangeException(nameof(request.ContextType));
            }

            // Generate OrderCode (PayOS uses int, not string)
            var orderCode = GenerateOrderCode();

            var payment = new Payment
            {
                Provider = PaymentProvider.PayOS,
                OrderId = orderCode.ToString(), // Store as string for compatibility
                RequestId = Guid.NewGuid().ToString(),
                Amount = request.Amount,
                Currency = "VND",
                Status = PaymentStatus.Pending,
                ContextType = request.ContextType,
                ContextId = contextId,
                Message = request.Description,
                ExtraData = request.ExtraData,
            };

            await _uow.Payments.AddAsync(payment, ct);

            // Build request and signature - signature goes in header, NOT in body
            var (payOSRequest, signature) = BuildCreateRequest(payment, request.Description, orderCode);

            // Debug: Log full request JSON (without signature in body)
            var requestJson = JsonSerializer.Serialize(payOSRequest, _jsonOptions);
            Console.WriteLine($"[CreatePaymentAsync] 📤 Full Request JSON (no signature in body): {requestJson}");

            await _uow.PaymentLogs.AddAsync(new PaymentLog
            {
                PaymentId = payment.Id,
                Event = "Create.Request",
                Payload = requestJson
            }, ct);

            // Use named HttpClient "PayOS" configured in Program.cs with SSL bypass
            // Named clients from IHttpClientFactory are managed and don't need disposal
            // IMPORTANT: Do NOT use DefaultRequestHeaders.Add() for PayOS headers
            // HttpClient is reused, DefaultRequestHeaders will accumulate and create duplicate headers
            // PayOS will receive multiple x-signature headers → code 20: invalid request
            var httpClient = _httpClientFactory.CreateClient("PayOS");
            Console.WriteLine("[CreatePaymentAsync] ✅ Using named HttpClient 'PayOS'");

            // PayOS v2 requires signature in HTTP header x-signature, NOT in JSON body
            // Use HttpRequestMessage.Headers (per-request) instead of DefaultRequestHeaders (shared)
            using var httpRequest = new HttpRequestMessage(HttpMethod.Post, _options.EndpointCreate);

            // Add headers to THIS request only (not shared across requests)
            httpRequest.Headers.Add("x-client-id", _options.ClientId);
            httpRequest.Headers.Add("x-api-key", _options.ApiKey);
            httpRequest.Headers.Add("x-signature", signature);
            httpRequest.Headers.Add("User-Agent", "TPEdu-API/1.0");

            httpRequest.Content = new StringContent(
                JsonSerializer.Serialize(payOSRequest, _jsonOptions),
                Encoding.UTF8,
                "application/json"
            );

            Console.WriteLine($"[CreatePaymentAsync] 🔐 x-signature header = [{signature}]");

            Console.WriteLine($"[CreatePaymentAsync] 🌐 Sending request to: {_options.EndpointCreate}");
            Console.WriteLine($"[CreatePaymentAsync] 🔧 HttpClient BaseAddress: {httpClient.BaseAddress}");
            Console.WriteLine($"[CreatePaymentAsync] 🔧 HttpClient Timeout: {httpClient.Timeout}");

            HttpResponseMessage response;
            string responseContent;

            try
            {
                response = await httpClient.SendAsync(httpRequest, ct);
                responseContent = await response.Content.ReadAsStringAsync(ct);
            }
             catch (HttpRequestException httpEx)
             {
                 // Log SSL/TLS connection errors in detail
                 Console.WriteLine($"[CreatePaymentAsync] ❌ HTTP Request Exception: {httpEx.Message}");
                 Console.WriteLine($"[CreatePaymentAsync] ❌ Inner Exception Type: {httpEx.InnerException?.GetType().Name ?? "None"}");
                 Console.WriteLine($"[CreatePaymentAsync] ❌ Inner Exception Message: {httpEx.InnerException?.Message ?? "None"}");
                 Console.WriteLine($"[CreatePaymentAsync] ❌ Inner Exception StackTrace: {httpEx.InnerException?.StackTrace ?? "None"}");
                 Console.WriteLine($"[CreatePaymentAsync] ❌ StackTrace: {httpEx.StackTrace}");
                 
                 // Check if it's a network/SSL issue
                 var innerEx = httpEx.InnerException;
                 var isSslError = innerEx != null && (
                     innerEx.Message.Contains("SSL") ||
                     innerEx.Message.Contains("TLS") ||
                     innerEx.Message.Contains("certificate") ||
                     innerEx.Message.Contains("forcibly closed") ||
                     innerEx.Message.Contains("transport connection")
                 );
                 
                 if (isSslError)
                 {
                     Console.WriteLine($"[CreatePaymentAsync] ⚠️ SSL/TLS Connection Error Detected");
                     Console.WriteLine($"[CreatePaymentAsync] 💡 Troubleshooting suggestions:");
                     Console.WriteLine($"[CreatePaymentAsync]   1. Check if firewall/antivirus is blocking HTTPS connections");
                     Console.WriteLine($"[CreatePaymentAsync]   2. Try disabling antivirus temporarily");
                     Console.WriteLine($"[CreatePaymentAsync]   3. Check if corporate proxy is blocking the connection");
                     Console.WriteLine($"[CreatePaymentAsync]   4. Verify PayOS endpoint is accessible: {_options.EndpointCreate}");
                     Console.WriteLine($"[CreatePaymentAsync]   5. Try using a different network (mobile hotspot)");
                 }
 
                 _logger.LogError(httpEx,
                     "SSL/TLS connection error when calling PayOS API. Endpoint: {Endpoint}, Error: {ErrorMessage}, Inner: {InnerException}, InnerType: {InnerType}",
                     _options.EndpointCreate, 
                     httpEx.Message, 
                     httpEx.InnerException?.Message,
                     httpEx.InnerException?.GetType().Name);
 
                 payment.Status = PaymentStatus.Failed;
                 await _uow.SaveChangesAsync();
 
                 var errorDetails = isSslError 
                     ? $"SSL/TLS connection error. This may be caused by firewall, antivirus, or network restrictions. " +
                       $"Try: 1) Disable antivirus temporarily, 2) Check firewall settings, 3) Try different network. " +
                       $"Endpoint: {_options.EndpointCreate}. Error: {httpEx.Message}. Inner: {httpEx.InnerException?.Message ?? "None"}"
                     : $"The SSL connection could not be established, see inner exception. " +
                       $"Endpoint: {_options.EndpointCreate}. Error: {httpEx.Message}. Inner: {httpEx.InnerException?.Message ?? "None"}";
 
                 throw new InvalidOperationException($"{errorDetails}. Please check the logs for more details.", httpEx);
             }
            catch (TaskCanceledException timeoutEx)
            {
                Console.WriteLine($"[CreatePaymentAsync] ❌ Request timeout: {timeoutEx.Message}");
                _logger.LogError(timeoutEx, "PayOS API request timeout. Endpoint: {Endpoint}", _options.EndpointCreate);

                payment.Status = PaymentStatus.Failed;
                await _uow.SaveChangesAsync();

                throw new InvalidOperationException(
                    $"Request to PayOS API timed out. Endpoint: {_options.EndpointCreate}. " +
                    $"Please check the logs for more details.", timeoutEx);
            }

            using (response)
            {
                Console.WriteLine($"[CreatePaymentAsync] 📥 Response Status: {response.StatusCode}");
                Console.WriteLine($"[CreatePaymentAsync] 📥 Response Body: {responseContent}");

                // Log nếu HTTP status không thành công
                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogError(
                        "PayOS API returned non-success status: {StatusCode}, Response: {ResponseContent}",
                        response.StatusCode, responseContent);
                    payment.Status = PaymentStatus.Failed;
                    payment.Message = $"PayOS API error: {response.StatusCode} - {responseContent}";
                    await _uow.SaveChangesAsync();
                    throw new InvalidOperationException($"PayOS API returned error: {response.StatusCode}. Response: {responseContent}");
                }

                await _uow.PaymentLogs.AddAsync(new PaymentLog
                {
                    PaymentId = payment.Id,
                    Event = "Create.Response",
                    Payload = responseContent
                }, ct);

                CreatePayOSPaymentResponseDto? payOSResponse;
                try
                {
                    payOSResponse = JsonSerializer.Deserialize<CreatePayOSPaymentResponseDto>(responseContent, _jsonOptions);
                    if (payOSResponse == null)
                    {
                        _logger.LogError("PayOS response is null. Response content: {ResponseContent}", responseContent);
                        throw new InvalidOperationException($"PayOS create payment response is invalid. Response: {responseContent}");
                    }
                }
                catch (JsonException ex)
                {
                    _logger.LogError(ex, "Failed to deserialize PayOS response. Response content: {ResponseContent}", responseContent);
                    throw new InvalidOperationException($"PayOS create payment response is invalid JSON. Response: {responseContent}. Error: {ex.Message}");
                }

                Console.WriteLine($"[CreatePaymentAsync] 📥 PayOS response: Code={payOSResponse.Code}, Desc={payOSResponse.Desc}");
                _logger.LogInformation(
                    "PayOS create payment response: Code={Code}, Desc={Desc}, OrderCode={OrderCode}",
                    payOSResponse.Code, payOSResponse.Desc, payOSResponse.Data?.OrderCode ?? 0);

                // PayOS uses string code, convert to int for ResultCode
                payment.ResultCode = int.TryParse(payOSResponse.Code, out var codeInt) ? codeInt : -1;
                payment.Message = payOSResponse.Desc;

                if (payOSResponse.Code != "00" || payOSResponse.Data == null)
                {
                    payment.Status = PaymentStatus.Failed;
                    await _uow.SaveChangesAsync();

                    var errorMessage = GetPayOSErrorMessage(payOSResponse.Code, payOSResponse.Desc);

                    _logger.LogError(
                        "PayOS create payment failed: Code={Code}, Desc={Desc}, OrderCode={OrderCode}, PaymentId={PaymentId}",
                        payOSResponse.Code, payOSResponse.Desc, orderCode, payment.Id);

                    Console.WriteLine($"[CreatePaymentAsync] ❌ PayOS create payment failed: {payOSResponse.Desc} (code {payOSResponse.Code})");

                    throw new InvalidOperationException(errorMessage);
                }

                // Validate QR code or checkout URL
                if (string.IsNullOrWhiteSpace(payOSResponse.Data.QrCode) &&
                    string.IsNullOrWhiteSpace(payOSResponse.Data.CheckoutUrl))
                {
                    payment.Status = PaymentStatus.Failed;
                    await _uow.SaveChangesAsync();
                    Console.WriteLine($"[CreatePaymentAsync] ❌ PayOS không trả về QR code hoặc CheckoutUrl. Response: {responseContent}");
                    _logger.LogError(
                        "PayOS create payment thành công (Code=00) nhưng không có QR code hoặc CheckoutUrl. PaymentId={PaymentId}, OrderCode={OrderCode}, Response={Response}",
                        payment.Id, orderCode, responseContent);
                    throw new InvalidOperationException("PayOS không trả về QR code hoặc CheckoutUrl. Không thể tạo payment link.");
                }

                await _uow.SaveChangesAsync();

                // Set PaymentId in response
                payOSResponse.PaymentId = payment.Id;

                var qrCodePreview = !string.IsNullOrWhiteSpace(payOSResponse.Data.QrCode)
                    ? payOSResponse.Data.QrCode.Substring(0, Math.Min(50, payOSResponse.Data.QrCode.Length)) + "..."
                    : "NULL";
                Console.WriteLine($"[CreatePaymentAsync] ✅ Tạo payment thành công: PaymentId={payment.Id}, OrderCode={orderCode}, QRCode={qrCodePreview}");

                return payOSResponse;
            }
        }
        catch (InvalidOperationException)
        {
            // Re-throw InvalidOperationException (validation errors, API errors)
            throw;
        }
        catch (ArgumentException)
        {
            // Re-throw ArgumentException (validation errors)
            throw;
        }
        catch (UnauthorizedAccessException)
        {
            // Re-throw UnauthorizedAccessException
            throw;
        }
        catch (Exception ex)
        {
            // Log unexpected errors
            _logger.LogError(ex,
                "Unexpected error in CreatePaymentAsync: {ErrorMessage}. StackTrace: {StackTrace}",
                ex.Message, ex.StackTrace);
            throw new InvalidOperationException(
                $"An unexpected error occurred while creating PayOS payment: {ex.Message}. " +
                "Please check the logs for more details.", ex);
        }
    }

    public async Task<PayOSIpnResponseDto> HandleIpnAsync(PayOSIpnRequestDto request, CancellationToken ct = default)
    {
        try
        {
            _logger.LogInformation(
                "🔔 [IPN] Nhận IPN từ PayOS: OrderCode={OrderCode}, Code={Code}, Desc={Desc}, Amount={Amount}",
                request.Data?.OrderCode, request.Code, request.Desc, request.Data?.Amount ?? 0);
            
            Console.WriteLine($"[IPN] 🔔 Nhận IPN từ PayOS - OrderCode: {request.Data?.OrderCode}, Code: {request.Code}, Amount: {request.Data?.Amount ?? 0}");
            
            if (request.Data == null || string.IsNullOrWhiteSpace(request.Data.OrderCode))
            {
                _logger.LogWarning("PayOS IPN không có Data hoặc OrderCode");
                return new PayOSIpnResponseDto { Code = "01", Desc = "INVALID_DATA" };
            }

            // Validate signature
            if (!ValidateIpnSignature(request))
            {
                _logger.LogWarning(
                    "⚠️ PayOS IPN signature không hợp lệ cho OrderCode {OrderCode}",
                    request.Data.OrderCode);
                return new PayOSIpnResponseDto { Code = "01", Desc = "INVALID_SIGNATURE" };
            }

            // Find payment by OrderCode
            var orderCodeStr = request.Data.OrderCode;
            var payment = await _uow.Payments
                .GetByOrderIdAsync(PaymentProvider.PayOS, orderCodeStr, ct);
            
            if (payment == null)
            {
                _logger.LogError(
                    "❌ LỖI: PayOS IPN received for unknown OrderCode {OrderCode}. Payment không tồn tại trong database.",
                    orderCodeStr);
                return new PayOSIpnResponseDto { Code = "01", Desc = "ORDER_NOT_FOUND" };
            }
            
            _logger.LogInformation(
                "Đã tìm thấy payment {PaymentId} cho OrderCode {OrderCode}. Status hiện tại: {Status}, ContextType: {ContextType}, ContextId: {ContextId}",
                payment.Id, orderCodeStr, payment.Status, payment.ContextType, payment.ContextId);

            // Create IPN log
            await _uow.PaymentLogs.AddAsync(new PaymentLog
            {
                PaymentId = payment.Id,
                Event = "IPN.Received",
                Payload = JsonSerializer.Serialize(request, _jsonOptions)
            }, ct);

            // Check if payment is already processed
            var isNewlyPaid = payment.Status != PaymentStatus.Paid;
            
            // Update payment status
            if (request.Code == "00" && request.Desc == "success")
            {
                payment.Status = PaymentStatus.Paid;
                payment.ResultCode = 0;
                payment.Message = "Thanh toán thành công";
                payment.PaidAt = DateTimeHelper.VietnamNow;
                
                if (!string.IsNullOrWhiteSpace(request.Data.Reference))
                {
                    payment.TransactionId = request.Data.Reference;
                }
                
                await _uow.SaveChangesAsync();
                
                // Apply business logic if newly paid
                if (isNewlyPaid)
                {
                    try
                    {
                        await ApplyPaymentSuccessAsync(payment, request, ct);
                        await _uow.SaveChangesAsync();
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Error applying payment success logic for payment {PaymentId}", payment.Id);
                        // Payment is already marked as Paid, so we continue
                    }
                }
            }
            else
            {
                payment.Status = PaymentStatus.Failed;
                payment.ResultCode = int.TryParse(request.Code, out var code) ? code : -1;
                payment.Message = request.Desc;
                await _uow.SaveChangesAsync();
            }

            return new PayOSIpnResponseDto
            {
                Code = "00",
                Desc = "success",
                Data = new PayOSIpnResponseData { OrderCode = request.Data.OrderCode }
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing PayOS IPN");
            return new PayOSIpnResponseDto { Code = "01", Desc = "INTERNAL_ERROR" };
        }
    }

    public async Task<PaymentStatusDto> GetPaymentStatusAsync(string paymentId, string userId, CancellationToken ct = default)
    {
        var payment = await _uow.Payments.GetByIdAsync(paymentId);
        if (payment == null)
        {
            throw new ArgumentException($"Payment not found with ID: {paymentId}", nameof(paymentId));
        }

        if (payment.ContextType == PaymentContextType.WalletDeposit && payment.ContextId != userId)
        {
            throw new UnauthorizedAccessException($"You can only view your own payments.");
        }

        var hasTransaction = await CheckIfBusinessLogicAppliedAsync(payment, ct);

        return new PaymentStatusDto
        {
            PaymentId = payment.Id,
            OrderId = payment.OrderId,
            Status = payment.Status.ToString(),
            Amount = payment.Amount,
            PaidAt = payment.PaidAt,
            HasTransaction = hasTransaction
        };
    }

    public async Task<OperationResult> RetryPaymentAsync(string paymentId, string userId, CancellationToken ct = default)
    {
        var payment = await _uow.Payments.GetByIdAsync(paymentId);
        if (payment == null)
        {
            return new OperationResult { Status = "Fail", Message = "Payment not found." };
        }

        if (payment.Provider != PaymentProvider.PayOS)
        {
            return new OperationResult { Status = "Fail", Message = "Payment is not a PayOS payment." };
        }

        if (payment.ContextType == PaymentContextType.WalletDeposit && payment.ContextId != userId)
        {
            return new OperationResult { Status = "Fail", Message = "You can only retry your own payments." };
        }

        if (payment.Status == PaymentStatus.Paid)
        {
            var hasBusinessLogicApplied = await CheckIfBusinessLogicAppliedAsync(payment, ct);
            if (hasBusinessLogicApplied)
            {
                return new OperationResult { Status = "Ok", Message = "Payment already processed successfully." };
            }
            
            // Retry applying business logic
            try
            {
                // Create a mock IPN request from payment data
                var mockIpn = new PayOSIpnRequestDto
                {
                    Code = "00",
                    Desc = "success",
                    Data = new PayOSIpnData
                    {
                        OrderCode = payment.OrderId,
                        Amount = payment.Amount,
                        Description = payment.Message ?? "Payment",
                        Reference = payment.TransactionId ?? string.Empty,
                        TransactionDateTime = payment.PaidAt?.ToString("yyyy-MM-dd HH:mm:ss") ?? DateTimeHelper.VietnamNow.ToString("yyyy-MM-dd HH:mm:ss"),
                        Currency = payment.Currency
                    }
                };
                
                await ApplyPaymentSuccessAsync(payment, mockIpn, ct);
                await _uow.SaveChangesAsync();
                
                return new OperationResult { Status = "Ok", Message = "Payment retry successful." };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error retrying payment {PaymentId}", paymentId);
                return new OperationResult { Status = "Fail", Message = $"Error: {ex.Message}" };
            }
        }

        return new OperationResult { Status = "Fail", Message = $"Payment status is {payment.Status}, cannot retry." };
    }

    public async Task<OperationResult> RetryPaymentByOrderIdAsync(int orderCode, string userId, CancellationToken ct = default)
    {
        var payment = await _uow.Payments.GetByOrderIdAsync(PaymentProvider.PayOS, orderCode.ToString(), ct);
        if (payment == null)
        {
            return new OperationResult { Status = "Fail", Message = "Payment not found." };
        }

        return await RetryPaymentAsync(payment.Id, userId, ct);
    }

    private int GenerateOrderCode()
    {
        // PayOS requires OrderCode to be a positive integer
        // Generate a unique order code using timestamp and random number
        var timestamp = (int)(DateTimeHelper.VietnamNow.Subtract(new DateTime(2020, 1, 1)).TotalSeconds);
        var random = new Random().Next(1000, 9999);
        return timestamp * 10000 + random;
    }

    private (object request, string signature) BuildCreateRequest(Payment payment, string? description, int orderCode)
    {
        var descriptionText = description ?? $"Payment for {payment.ContextType}";
        if (descriptionText.Length > 255)
        {
            descriptionText = descriptionText.Substring(0, 255);
        }

        var amount = (long)payment.Amount;
        
        // Normalize data before building signature
        // CRITICAL: PayOS is very strict - any character mismatch will cause code 20
        // 1. Remove Vietnamese accents (PayOS recommendation: avoid UTF-8 in HMAC)
        // 2. Trim whitespace and newlines
        // 3. Replace spaces with underscores (PayOS may have issues with spaces)
        // 4. Ensure URLs match exactly with appsettings (DO NOT remove trailing slash if it exists in appsettings)
        var normalizedDescription = RemoveVietnameseAccents(descriptionText).Trim()
            .Replace(" ", "_"); // Replace spaces with underscores for signature 
        
        // Normalize URLs: trim, remove newlines/tabs, replace backslashes
        // PayOS is very strict - URLs must match exactly
        var normalizedCancelUrl = _options.CancelUrl.Trim()
            .Replace("\n", "")
            .Replace("\r", "")
            .Replace("\t", "")
            .Replace("\\", "/"); // Replace backslashes with forward slashes
            //.TrimEnd('/'); // REMOVED: Do not remove trailing slash, must match PayOS dashboard exactly
        
        var normalizedReturnUrl = _options.ReturnUrl.Trim()
            .Replace("\n", "")
            .Replace("\r", "")
            .Replace("\t", "")
            .Replace("\\", "/"); // Replace backslashes with forward slashes
            //.TrimEnd('/'); // REMOVED: Do not remove trailing slash, must match PayOS dashboard exactly
        
        // Build signature string for PayOS
        // PayOS signature format: key=value&key=value&... (alphabetical order)
        // Format: amount=value&cancelUrl=value&description=value&orderCode=value&returnUrl=value
        // IMPORTANT: 
        // - Do NOT include items in signature - PayOS v2 does not require items for create payment
        // - Do NOT include expiredAt - PayOS backend rejects this field for some merchant accounts (code 20)
        var signatureData = 
            $"amount={amount}" +
            $"&cancelUrl={normalizedCancelUrl}" +
            $"&description={normalizedDescription}" +
            $"&orderCode={orderCode}" +
            $"&returnUrl={normalizedReturnUrl}";
        
        var signature = ComputePayOSSignature(signatureData);
        
        // Log PayOS configuration for debugging (Debug level only)
        _logger.LogDebug("[BuildCreateRequest] PayOS Config: ClientId={ClientId}, Endpoint={Endpoint}, CancelUrl={CancelUrl}, ReturnUrl={ReturnUrl}",
            _options.ClientId, _options.EndpointCreate, _options.CancelUrl, _options.ReturnUrl);
        
        // Log signature components (Debug level - sensitive data)
        _logger.LogDebug(
            "[BuildCreateRequest] Signature components: amount={Amount} (type: {AmountType}), " +
            "cancelUrl length={CancelUrlLength}, description length={DescLength}, " +
            "orderCode={OrderCode} (type: {OrderCodeType}), returnUrl length={ReturnUrlLength}, " +
            "signatureData length={SignatureDataLength}, checksumKey length={ChecksumKeyLength}",
            amount, amount.GetType().Name,
            normalizedCancelUrl.Length, normalizedDescription.Length,
            orderCode, orderCode.GetType().Name, normalizedReturnUrl.Length,
            signatureData.Length, _options.ChecksumKey?.Length ?? 0);
        
        // Only log exact signature string in Development (sensitive data)
        if (System.Diagnostics.Debugger.IsAttached || Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT") == "Development")
        {
            Console.WriteLine($"[BuildCreateRequest] 🔐 SignatureData (EXACT STRING FOR PAYOS) = [{signatureData}]");
            Console.WriteLine($"[BuildCreateRequest] 🔐 Computed signature (hex) = [{signature}]");
        }

        // Build request body - INCLUDE signature in body
        // PayOS v2 requires signature in BOTH HTTP header AND JSON body (per official docs)
        // See: https://payos.vn/docs/api/#tao-link-thanh-toan
        var request = new
        {
            orderCode = orderCode,
            amount = amount,
            description = normalizedDescription,
            cancelUrl = normalizedCancelUrl,
            returnUrl = normalizedReturnUrl,
            signature = signature // CRITICAL: signature MUST be in body per PayOS docs
        };
        
        // Log JSON body that will be sent to PayOS (Debug level only)
        var requestJson = JsonSerializer.Serialize(request, _jsonOptions);
        _logger.LogDebug("[BuildCreateRequest] JSON body to PayOS (with signature): {RequestJson}", requestJson);

        return (request, signature);
    }

    private string ComputePayOSSignature(string data)
    {
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(_options.ChecksumKey));
        var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(data));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    /// <summary>
    /// Remove Vietnamese accents to avoid encoding issues with PayOS signature validation.
    /// PayOS is very strict and may reject descriptions with special characters.
    /// </summary>
    private static string RemoveVietnameseAccents(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return text;

        var normalized = text.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder();

        foreach (var c in normalized)
        {
            var unicodeCategory = CharUnicodeInfo.GetUnicodeCategory(c);
            if (unicodeCategory != UnicodeCategory.NonSpacingMark)
            {
                sb.Append(c);
            }
        }

        return sb.ToString().Normalize(NormalizationForm.FormC);
    }

    private bool ValidateIpnSignature(PayOSIpnRequestDto request)
    {
        if (request.Data == null) return false;
        
        // PayOS signature validation
        // According to PayOS docs, signature is computed from: code + desc + data (JSON string)
        // But for IPN, PayOS sends signature in the request
        // We need to verify it matches our computed signature
        // For now, we'll validate based on PayOS documentation
        // Note: PayOS may send signature in different format, adjust as needed
        
        try
        {
            var dataJson = JsonSerializer.Serialize(request.Data, _jsonOptions);
            var rawString = $"code={request.Code}&desc={request.Desc}&data={dataJson}";
            
            using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(_options.ChecksumKey));
            var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(rawString));
            var computedSignature = Convert.ToHexString(hash).ToLowerInvariant();
            
            return string.Equals(computedSignature, request.Signature, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error validating PayOS IPN signature");
            return false;
        }
    }

    private async Task ApplyPaymentSuccessAsync(Payment payment, PayOSIpnRequestDto request, CancellationToken ct)
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

    private async Task ApplyEscrowPaymentAsync(Payment payment, PayOSIpnRequestDto request, CancellationToken ct)
    {
        var escrow = await _uow.Escrows.GetByIdAsync(payment.ContextId, ct);
        if (escrow == null)
        {
            _logger.LogWarning("Escrow {EscrowId} not found when processing PayOS payment {PaymentId}", payment.ContextId, payment.Id);
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
            Note = $"PayOS escrow payment {payment.OrderId}",
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

    private async Task ApplyWalletDepositAsync(Payment payment, PayOSIpnRequestDto request, CancellationToken ct)
    {
        _logger.LogInformation(
            "Bắt đầu cộng tiền vào ví cho payment {PaymentId} (OrderCode: {OrderCode}, UserId: {UserId}, Amount: {Amount})",
            payment.Id, payment.OrderId, payment.ContextId, payment.Amount);

        try
        {
            if (string.IsNullOrWhiteSpace(payment.ContextId))
            {
                _logger.LogError(
                    "Payment {PaymentId} (OrderCode: {OrderCode}) có ContextId rỗng. Không thể cộng tiền vào ví.",
                    payment.Id, payment.OrderId);
                throw new ArgumentException($"Payment {payment.Id} has empty ContextId", nameof(payment));
            }
            
            var wallet = await _walletService.GetMyWalletAsync(payment.ContextId, ct);
            
            if (wallet == null)
            {
                _logger.LogError(
                    "Không thể lấy hoặc tạo wallet cho user {UserId} (Payment {PaymentId}, OrderCode: {OrderCode})",
                    payment.ContextId, payment.Id, payment.OrderId);
                throw new InvalidOperationException($"Cannot get or create wallet for user {payment.ContextId}");
            }

            var trackedWallet = await _uow.Wallets.GetByIdAsync(wallet.Id);
            if (trackedWallet == null)
            {
                _logger.LogError(
                    "Không tìm thấy wallet {WalletId} trong database sau khi GetMyWalletAsync cho payment {PaymentId}",
                    wallet.Id, payment.Id);
                throw new InvalidOperationException($"Wallet {wallet.Id} not found in database");
            }
            
            var oldBalance = trackedWallet.Balance;
            trackedWallet.Balance += payment.Amount;
            await _uow.Wallets.Update(trackedWallet);
            
            _logger.LogInformation(
                "Đã cập nhật số dư ví: {OldBalance} -> {NewBalance} (+{Amount})",
                oldBalance, trackedWallet.Balance, payment.Amount);

            var reference = request.Data?.Reference ?? payment.TransactionId ?? string.Empty;
            var note = !string.IsNullOrWhiteSpace(reference)
                ? $"PayOS wallet deposit {payment.OrderId} (Ref: {reference})"
                : $"PayOS wallet deposit {payment.OrderId}";
            
            var transaction = new Transaction
            {
                WalletId = trackedWallet.Id,
                Type = TransactionType.Credit,
                Status = TransactionStatus.Succeeded,
                Amount = payment.Amount,
                Note = note,
                CounterpartyUserId = payment.ContextId
            };

            await _uow.Transactions.AddAsync(transaction, ct);
            
            _logger.LogInformation(
                "Đã tạo transaction cho wallet {WalletId}. TransactionId={TransactionId}, Amount={Amount}",
                trackedWallet.Id, transaction.Id, transaction.Amount);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "LỖI NGHIÊM TRỌNG khi cộng tiền vào ví cho payment {PaymentId} (OrderCode: {OrderCode}, UserId: {UserId}, Amount: {Amount})",
                payment.Id, payment.OrderId, payment.ContextId, payment.Amount);
            throw;
        }
    }

    private async Task<Wallet> CreateWalletAsync(string userId, CancellationToken ct)
    {
        var wallet = new Wallet { UserId = userId, Balance = 0m, Currency = "VND", IsFrozen = false };
        await _uow.Wallets.AddAsync(wallet, ct);
        await _uow.SaveChangesAsync();
        return wallet;
    }

    private async Task<bool> CheckIfBusinessLogicAppliedAsync(Payment payment, CancellationToken ct)
    {
        try
        {
            switch (payment.ContextType)
            {
                case PaymentContextType.WalletDeposit:
                    var wallet = await _walletService.GetMyWalletAsync(payment.ContextId, ct);
                    var (transactions, _) = await _uow.Transactions.GetByWalletIdAsync(wallet.Id, 1, 10, ct);
                    return transactions.Any(t => 
                        t.Type == TransactionType.Credit && 
                        t.Status == TransactionStatus.Succeeded &&
                        t.Note != null && 
                        t.Note.Contains(payment.OrderId));

                case PaymentContextType.Escrow:
                    var escrow = await _uow.Escrows.GetByIdAsync(payment.ContextId, ct);
                    return escrow != null && escrow.Status == EscrowStatus.Held;

                default:
                    return false;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error checking if business logic applied for payment {PaymentId}", payment.Id);
            return false;
        }
    }

    private void ValidatePayOSConfiguration()
    {
        var errors = new List<string>();
        
        if (string.IsNullOrWhiteSpace(_options.ClientId))
            errors.Add("ClientId is missing or empty");
        
        if (string.IsNullOrWhiteSpace(_options.ApiKey))
            errors.Add("ApiKey is missing or empty");
        
        if (string.IsNullOrWhiteSpace(_options.ChecksumKey))
            errors.Add("ChecksumKey is missing or empty");
        
        if (string.IsNullOrWhiteSpace(_options.EndpointCreate))
            errors.Add("EndpointCreate is missing or empty");
        
        if (string.IsNullOrWhiteSpace(_options.ReturnUrl))
            errors.Add("ReturnUrl is missing or empty");
        
        if (string.IsNullOrWhiteSpace(_options.CancelUrl))
            errors.Add("CancelUrl is missing or empty");
        
        if (errors.Count > 0)
        {
            var errorMessage = $"PayOS configuration is invalid: {string.Join(", ", errors)}. " +
                              "Please check your appsettings.json file.";
            _logger.LogError(errorMessage);
            throw new InvalidOperationException(errorMessage);
        }
    }

    private string GetPayOSErrorMessage(string code, string? originalMessage)
    {
        var baseMessage = originalMessage ?? "Unknown error";
        
        return code switch
        {
            "00" => "Success",
            "01" => $"PayOS error: {baseMessage}. Please check your payment details and try again.",
            _ => $"PayOS payment error (code {code}): {baseMessage}. Please contact support for assistance."
        };
    }
}

