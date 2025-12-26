using BusinessLayer.DTOs.API;
using BusinessLayer.DTOs.Wallet;
using BusinessLayer.Service.Interface;
using DataLayer.Enum;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System;
using System.Linq;
using System.Threading.Tasks;

namespace TPEdu_API.Controllers
{
    [ApiController]
    [Route("tpedu/v1/admin/transactions")]
    [Authorize(Roles = "Admin")]
    public class AdminTransactionController : ControllerBase
    {
        private readonly IWalletService _walletService;

        public AdminTransactionController(IWalletService walletService)
        {
            _walletService = walletService;
        }

        /// <summary>
        /// Admin lấy danh sách giao dịch với filter
        /// </summary>
        [HttpGet]
        public async Task<IActionResult> GetTransactions(
            [FromQuery] string? role,
            [FromQuery] string? type,
            [FromQuery] string? status,
            [FromQuery] DateTime? startDate,
            [FromQuery] DateTime? endDate,
            [FromQuery] int page = 1,
            [FromQuery] int pageSize = 50)
        {
            try
            {
                // Parse enums
                TransactionType? transactionType = null;
                if (!string.IsNullOrEmpty(type) && Enum.TryParse<TransactionType>(type, out var parsedType))
                {
                    transactionType = parsedType;
                }

                TransactionStatus? transactionStatus = null;
                if (!string.IsNullOrEmpty(status) && Enum.TryParse<TransactionStatus>(status, out var parsedStatus))
                {
                    transactionStatus = parsedStatus;
                }

                var result = await _walletService.GetTransactionsForAdminAsync(
                    role,
                    transactionType,
                    transactionStatus,
                    startDate,
                    endDate,
                    page,
                    pageSize);
                var items = result.items;
                var total = result.total;

                var transactionList = new System.Collections.Generic.List<TransactionDto>();
                foreach (var t in items)
                {
                    string? userName = null;
                    string? userEmail = null;
                    if (t.Wallet?.User != null)
                    {
                        userName = t.Wallet.User.UserName;
                        userEmail = t.Wallet.User.Email;
                    }

                    string? counterpartyUsername = null;
                    if (!string.IsNullOrEmpty(t.CounterpartyUserId))
                    {
                        counterpartyUsername = await _walletService.GetUsernameByUserIdAsync(t.CounterpartyUserId);
                    }

                    transactionList.Add(new TransactionDto
                    {
                        Id = t.Id,
                        WalletId = t.WalletId,
                        UserId = t.Wallet?.UserId,
                        UserName = userName,
                        UserEmail = userEmail,
                        Type = t.Type.ToString(),
                        Amount = t.Amount,
                        Status = t.Status.ToString(),
                        Note = t.Note,
                        CounterpartyUserId = t.CounterpartyUserId,
                        CounterpartyUsername = counterpartyUsername,
                        CreatedAt = t.CreatedAt
                    });
                }

                return Ok(ApiResponse<object>.Ok(new
                {
                    items = transactionList,
                    total,
                    page,
                    size = pageSize
                }, "Lấy danh sách giao dịch thành công"));
            }
            catch (Exception ex)
            {
                return StatusCode(500, ApiResponse<object>.Fail($"Lỗi hệ thống: {ex.Message}"));
            }
        }

        /// <summary>
        /// Admin lấy chi tiết giao dịch
        /// </summary>
        [HttpGet("{transactionId}")]
        public async Task<IActionResult> GetTransactionDetail(string transactionId)
        {
            try
            {
                var transaction = await _walletService.GetTransactionDetailForAdminAsync(transactionId);
                if (transaction == null)
                {
                    return NotFound(ApiResponse<object>.Fail("Không tìm thấy giao dịch"));
                }

                string? userName = null;
                string? userEmail = null;
                if (transaction.Wallet?.User != null)
                {
                    userName = transaction.Wallet.User.UserName;
                    userEmail = transaction.Wallet.User.Email;
                }

                string? counterpartyUsername = null;
                if (!string.IsNullOrEmpty(transaction.CounterpartyUserId))
                {
                    counterpartyUsername = await _walletService.GetUsernameByUserIdAsync(transaction.CounterpartyUserId);
                }

                var dto = new TransactionDto
                {
                    Id = transaction.Id,
                    WalletId = transaction.WalletId,
                    UserId = transaction.Wallet?.UserId,
                    UserName = userName,
                    UserEmail = userEmail,
                    Type = transaction.Type.ToString(),
                    Amount = transaction.Amount,
                    Status = transaction.Status.ToString(),
                    Note = transaction.Note,
                    CounterpartyUserId = transaction.CounterpartyUserId,
                    CounterpartyUsername = counterpartyUsername,
                    CreatedAt = transaction.CreatedAt
                };

                return Ok(ApiResponse<TransactionDto>.Ok(dto, "Lấy chi tiết giao dịch thành công"));
            }
            catch (Exception ex)
            {
                return StatusCode(500, ApiResponse<object>.Fail($"Lỗi hệ thống: {ex.Message}"));
            }
        }
    }
}

