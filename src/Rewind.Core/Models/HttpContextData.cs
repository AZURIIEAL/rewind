namespace Rewind.Core.Models;

public class HttpContextData
{
    public string Method { get; set; } = string.Empty;
    public string Path { get; set; } = string.Empty;
    public string QueryString { get; set; } = string.Empty;
    public string? RoutePattern { get; set; }
    public Dictionary<string, string> RouteValues { get; set; } = new();
    public Dictionary<string, string> Headers { get; set; } = new();
    public string? RawBodyPayload { get; set; }

    // Set when the request had a body that was deliberately not captured (too large, binary content type, ...).
    public string? BodyOmittedReason { get; set; }
}
