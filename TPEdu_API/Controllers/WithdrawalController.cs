using BusinessLayer.DTOs.API;
using BusinessLayer.DTOs.Wallet;
using BusinessLayer.Service.Interface;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System;
using System.Security.Claims;
using System.Threading.Tasks;
using TPEdu_API.Common.Extensions;

namespace TPEdu_API.Controllers;

[ApiController]
[Route("tpedu/v1/withdrawals")]
[Authorize]
public class WithdrawalController : ControllerBase
{
    private readonly IWithdrawalService _withdrawalService;

    public WithdrawalController(IWithdrawalService withdrawalService)
    {
        _withdrawalService = withdrawalService;
    }

    /// <summary>
    /// Tạo yêu cầu rút tiền (tất cả các role đều có thể sử dụng: Student, Tutor, Parent, Admin)
    /// </summary>
    [HttpPost]
    public async Task<IActionResult> CreateWithdrawalRequest([FromBody] CreateWithdrawalRequestDto dto)
    {
        var userId = User.RequireUserId();
        var result = await _withdrawalService.CreateWithdrawalRequestAsync(userId, dto);
        
        if (result.Status == "Fail")
            return BadRequest(ApiResponse<object>.Fail(result.Message));
        
        return Ok(ApiResponse<object>.Ok(result.Data, result.Message));
    }

    /// <summary>
    /// Xem danh sách yêu cầu rút tiền của mình (tất cả các role: Student, Tutor, Parent, Admin)
    /// </summary>
    [HttpGet("me")]
    public async Task<IActionResult> GetMyWithdrawalRequests([FromQuery] int pageNumber = 1, [FromQuery] int pageSize = 20)
    {
        var userId = User.RequireUserId();
        var (items, total) = await _withdrawalService.GetMyWithdrawalRequestsAsync(userId, pageNumber, pageSize);
        
        return Ok(new { items, page = pageNumber, size = pageSize, total });
    }

    /// <summary>
    /// Xem chi tiết yêu cầu rút tiền của mình (tất cả các role: Student, Tutor, Parent, Admin)
    /// </summary>
    [HttpGet("me/{requestId}")]
    public async Task<IActionResult> GetMyWithdrawalRequestById(string requestId)
    {
        var userId = User.RequireUserId();
        var request = await _withdrawalService.GetMyWithdrawalRequestByIdAsync(userId, requestId);
        
        if (request == null)
            return NotFound(ApiResponse<object>.Fail("Không tìm thấy yêu cầu rút tiền"));
        
        return Ok(ApiResponse<WithdrawalRequestDto>.Ok(request));
    }

    /// <summary>
    /// Hủy yêu cầu rút tiền của mình (chỉ khi Status = Pending). Tất cả các role đều có thể hủy yêu cầu của mình
    /// </summary>
    [HttpDelete("{requestId}")]
    public async Task<IActionResult> CancelWithdrawalRequest(string requestId)
    {
        var userId = User.RequireUserId();
        var result = await _withdrawalService.CancelWithdrawalRequestAsync(userId, requestId);
        
        if (result.Status == "Fail")
            return BadRequest(ApiResponse<object>.Fail(result.Message));
        
        return Ok(ApiResponse<object>.Ok(null, result.Message));
    }

    /// <summary>
    /// Admin: Xem danh sách tất cả yêu cầu rút tiền
    /// </summary>
    [HttpGet]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> GetAllWithdrawalRequests(
        [FromQuery] string? status,
        [FromQuery] int pageNumber = 1,
        [FromQuery] int pageSize = 20)
    {
        var (items, total) = await _withdrawalService.GetAllWithdrawalRequestsAsync(status, pageNumber, pageSize);
        
        return Ok(new { items, page = pageNumber, size = pageSize, total });
    }

    /// <summary>
    /// Admin: Xem chi tiết yêu cầu rút tiền
    /// </summary>
    [HttpGet("{requestId}")]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> GetWithdrawalRequestById(string requestId)
    {
        var request = await _withdrawalService.GetWithdrawalRequestByIdAsync(requestId);
        
        if (request == null)
            return NotFound(ApiResponse<object>.Fail("Không tìm thấy yêu cầu rút tiền"));
        
        return Ok(ApiResponse<WithdrawalRequestDto>.Ok(request));
    }

    /// <summary>
    /// Admin: Duyệt yêu cầu rút tiền và xử lý chuyển tiền
    /// </summary>
    [HttpPost("{requestId}/approve")]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> ApproveWithdrawalRequest(string requestId, [FromBody] ApproveWithdrawalRequestDto dto)
    {
        var adminUserId = User.RequireUserId();
        var result = await _withdrawalService.ApproveWithdrawalRequestAsync(adminUserId, requestId, dto);
        
        if (result.Status == "Fail")
            return BadRequest(ApiResponse<object>.Fail(result.Message));
        
        return Ok(ApiResponse<object>.Ok(null, result.Message));
    }

    /// <summary>
    /// Admin: Từ chối yêu cầu rút tiền
    /// </summary>
    [HttpPost("{requestId}/reject")]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> RejectWithdrawalRequest(string requestId, [FromBody] RejectWithdrawalRequestDto dto)
    {
        var adminUserId = User.RequireUserId();
        var result = await _withdrawalService.RejectWithdrawalRequestAsync(adminUserId, requestId, dto);
        
        if (result.Status == "Fail")
            return BadRequest(ApiResponse<object>.Fail(result.Message));
        
        return Ok(ApiResponse<object>.Ok(null, result.Message));
    }
}

