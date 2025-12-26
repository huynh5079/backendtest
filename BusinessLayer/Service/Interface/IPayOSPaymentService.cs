using System.Threading;
using System.Threading.Tasks;
using BusinessLayer.DTOs.Payment;
using BusinessLayer.DTOs.Wallet;

namespace BusinessLayer.Service.Interface;

public interface IPayOSPaymentService
{
    Task<CreatePayOSPaymentResponseDto> CreatePaymentAsync(CreatePayOSPaymentRequestDto request, string userId, CancellationToken ct = default);

    Task<PayOSIpnResponseDto> HandleIpnAsync(PayOSIpnRequestDto request, CancellationToken ct = default);

    Task<PaymentStatusDto> GetPaymentStatusAsync(string paymentId, string userId, CancellationToken ct = default);

    Task<OperationResult> RetryPaymentAsync(string paymentId, string userId, CancellationToken ct = default);
    
    Task<OperationResult> RetryPaymentByOrderIdAsync(int orderCode, string userId, CancellationToken ct = default);
}

