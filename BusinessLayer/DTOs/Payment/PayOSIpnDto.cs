namespace BusinessLayer.DTOs.Payment;

public class PayOSIpnRequestDto
{
    public string Code { get; set; } = default!;
    public string Desc { get; set; } = default!;
    public PayOSIpnData? Data { get; set; }
    public string Signature { get; set; } = default!;
}

public class PayOSIpnData
{
    public string OrderCode { get; set; } = default!;
    public decimal Amount { get; set; }
    public string Description { get; set; } = default!;
    public string AccountNumber { get; set; } = default!;
    public string Reference { get; set; } = default!;
    public string TransactionDateTime { get; set; } = default!;
    public string Currency { get; set; } = "VND";
    public string PaymentLinkId { get; set; } = default!;
    public string Code { get; set; } = default!;
    public string Desc { get; set; } = default!;
    public int CounterAccountBankId { get; set; }
    public string CounterAccountBankName { get; set; } = default!;
    public string CounterAccountName { get; set; } = default!;
    public string CounterAccountNumber { get; set; } = default!;
    public string VirtualAccountName { get; set; } = default!;
    public string VirtualAccountNumber { get; set; } = default!;
}

public class PayOSIpnResponseDto
{
    public string Code { get; set; } = "00";
    public string Desc { get; set; } = "success";
    public PayOSIpnResponseData? Data { get; set; }
}

public class PayOSIpnResponseData
{
    public string OrderCode { get; set; } = default!;
}

