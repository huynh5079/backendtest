namespace BusinessLayer.Options;

public class PayOSOptions
{
    public string ClientId { get; set; } = default!;
    public string ApiKey { get; set; } = default!;
    public string ChecksumKey { get; set; } = default!;
    public string EndpointCreate { get; set; } = default!;
    public string EndpointGet { get; set; } = default!;
    public string EndpointCancel { get; set; } = default!;
    public string ReturnUrl { get; set; } = default!;
    public string CancelUrl { get; set; } = default!;
}

