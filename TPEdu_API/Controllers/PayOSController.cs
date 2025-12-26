using BusinessLayer.DTOs.Payment;
using BusinessLayer.Service.Interface;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.Linq;
using TPEdu_API.Common.Extensions;

namespace TPEdu_API.Controllers;

[ApiController]
[Route("tpedu/v1/payments/payos")]
public class PayOSController : ControllerBase
{
    private readonly IPayOSPaymentService _payOSPaymentService;

    public PayOSController(IPayOSPaymentService payOSPaymentService)
    {
        _payOSPaymentService = payOSPaymentService;
    }

    /// <summary>
    /// Tạo đơn thanh toán PayOS cho Escrow/WalletDeposit.
    /// </summary>
    [HttpPost("create")]
    [Authorize]
    public async Task<IActionResult> CreatePayment([FromBody] CreatePayOSPaymentRequestDto request, CancellationToken ct)
    {
        try
        {
            // Log raw request for debugging
            Console.WriteLine($"[CreatePayment] 📥 Raw request received");
            Console.WriteLine($"[CreatePayment] 📥 ModelState.IsValid: {ModelState.IsValid}");
            
            if (!ModelState.IsValid)
            {
                var errors = ModelState.Values.SelectMany(v => v.Errors).Select(e => e.ErrorMessage);
                var errorKeys = ModelState.Keys.Where(k => ModelState[k]?.Errors?.Count > 0);
                Console.WriteLine($"[CreatePayment] ❌ Model validation failed:");
                Console.WriteLine($"[CreatePayment] ❌ Error keys: {string.Join(", ", errorKeys)}");
                Console.WriteLine($"[CreatePayment] ❌ Error messages: {string.Join(", ", errors)}");
                
                // Log each field value
                Console.WriteLine($"[CreatePayment] 📥 Request values: Amount={request.Amount}, ContextType={request.ContextType}, ContextId={request.ContextId ?? "null"}, Description={request.Description ?? "null"}, ExtraData={request.ExtraData ?? "null"}");
                
                return BadRequest(new { Status = "Fail", Message = "Validation failed", Errors = errors, ErrorKeys = errorKeys });
            }

            Console.WriteLine($"[CreatePayment] ✅ Valid request: Amount={request.Amount}, ContextType={request.ContextType}, ContextId={request.ContextId ?? "null"}, Description={request.Description ?? "null"}");
            
            var userId = User.RequireUserId();
            var response = await _payOSPaymentService.CreatePaymentAsync(request, userId, ct);
            return Ok(response);
        }
        catch (InvalidOperationException ex)
        {
            Console.WriteLine($"[CreatePayment] ❌ PayOS Payment Error: {ex.Message}");
            return BadRequest(new 
            { 
                Status = "Fail", 
                Message = ex.Message,
                ErrorType = "PayOSPaymentError"
            });
        }
        catch (ArgumentException ex)
        {
            Console.WriteLine($"[CreatePayment] ❌ Validation Error: {ex.Message}");
            return BadRequest(new 
            { 
                Status = "Fail", 
                Message = ex.Message,
                ErrorType = "ValidationError"
            });
        }
        catch (UnauthorizedAccessException ex)
        {
            Console.WriteLine($"[CreatePayment] ❌ Authorization Error: {ex.Message}");
            return Forbid(ex.Message);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[CreatePayment] ❌ Unexpected Exception: {ex.Message}");
            Console.WriteLine($"[CreatePayment] ❌ StackTrace: {ex.StackTrace}");
            return StatusCode(500, new 
            { 
                Status = "Fail", 
                Message = "An unexpected error occurred while processing your payment. Please try again later or contact support.",
                ErrorType = "InternalError"
            });
        }
    }

    /// <summary>
    /// PayOS IPN callback. Không yêu cầu xác thực.
    /// </summary>
    [HttpPost("ipn")]
    [AllowAnonymous]
    public async Task<IActionResult> HandleIpn([FromBody] PayOSIpnRequestDto request, CancellationToken ct)
    {
        Console.WriteLine($"[IPN] ✅ Nhận IPN từ PayOS: OrderCode={request.Data?.OrderCode}, Code={request.Code}, Amount={request.Data?.Amount ?? 0}");
        
        var response = await _payOSPaymentService.HandleIpnAsync(request, ct);
        
        if (response.Code == "00")
        {
            Console.WriteLine($"[IPN] ✅ Xử lý IPN thành công: OrderCode={request.Data?.OrderCode}");
        }
        else
        {
            Console.WriteLine($"[IPN] ❌ Xử lý IPN thất bại: OrderCode={request.Data?.OrderCode}, Message={response.Desc}");
        }
        
        return Ok(response);
    }

    /// <summary>
    /// Retry payment processing cho payment của user hiện tại.
    /// </summary>
    [HttpPost("{paymentId}/retry")]
    [Authorize]
    public async Task<IActionResult> RetryPayment(string paymentId, CancellationToken ct)
    {
        try
        {
            var userId = User.RequireUserId();
            Console.WriteLine($"[RetryPayment] 🔄 User {userId} đang retry payment {paymentId}");
            
            var response = await _payOSPaymentService.RetryPaymentAsync(paymentId, userId, ct);
            
            if (response.Status == "Ok")
            {
                Console.WriteLine($"[RetryPayment] ✅ Thành công: Payment {paymentId} đã được xử lý và cộng tiền");
            }
            else
            {
                Console.WriteLine($"[RetryPayment] ❌ Thất bại: Payment {paymentId}, Message: {response.Message}");
            }
            
            if (response.Status == "Fail")
                return BadRequest(response);
            return Ok(response);
        }
        catch (ArgumentException ex)
        {
            Console.WriteLine($"[RetryPayment] ❌ BadRequest: Payment {paymentId}, Error: {ex.Message}");
            return BadRequest(new { Status = "Fail", Message = ex.Message });
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[RetryPayment] ❌ Error: Payment {paymentId}, Error: {ex.Message}");
            return StatusCode(500, new { Status = "Fail", Message = $"Lỗi hệ thống: {ex.Message}" });
        }
    }

    /// <summary>
    /// Retry payment processing bằng OrderCode.
    /// </summary>
    [HttpPost("retry-by-order/{orderCode}")]
    [Authorize]
    public async Task<IActionResult> RetryPaymentByOrderCode(int orderCode, CancellationToken ct)
    {
        try
        {
            var userId = User.RequireUserId();
            var response = await _payOSPaymentService.RetryPaymentByOrderIdAsync(orderCode, userId, ct);
            if (response.Status == "Fail")
                return BadRequest(response);
            return Ok(response);
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { Status = "Fail", Message = ex.Message });
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { Status = "Fail", Message = $"Lỗi hệ thống: {ex.Message}" });
        }
    }

    /// <summary>
    /// Lấy trạng thái payment của user (để biết thanh toán thành công chưa).
    /// </summary>
    [HttpGet("status/{paymentId}")]
    [Authorize]
    public async Task<IActionResult> GetPaymentStatus(string paymentId, CancellationToken ct)
    {
        try
        {
            var userId = User.RequireUserId();
            Console.WriteLine($"[GetPaymentStatus] Request: PaymentId={paymentId}, UserId={userId}");
            
            var response = await _payOSPaymentService.GetPaymentStatusAsync(paymentId, userId, ct);
            
            Console.WriteLine($"[GetPaymentStatus] Success: PaymentId={paymentId}, Status={response.Status}, HasTransaction={response.HasTransaction}");
            return Ok(response);
        }
        catch (ArgumentException ex)
        {
            Console.WriteLine($"[GetPaymentStatus] Payment not found: PaymentId={paymentId}, Error={ex.Message}");
            return NotFound(new { Status = "Fail", Message = ex.Message });
        }
        catch (UnauthorizedAccessException ex)
        {
            Console.WriteLine($"[GetPaymentStatus] Unauthorized: PaymentId={paymentId}, Error={ex.Message}");
            return Forbid(ex.Message);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[GetPaymentStatus] Error: PaymentId={paymentId}, Error={ex.Message}");
            return StatusCode(500, new { Status = "Fail", Message = $"Lỗi hệ thống: {ex.Message}" });
        }
    }
}

