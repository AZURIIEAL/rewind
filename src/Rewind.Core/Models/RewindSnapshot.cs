namespace Rewind.Core.Models;

public class RewindSnapshot
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public DateTimeOffset Timestamp { get; set; } = DateTimeOffset.UtcNow;
    public string Environment { get; set; } = string.Empty;
    public string? ApplicationName { get; set; }
    public string? TraceId { get; set; }
    public ExceptionData? Exception { get; set; }
    public HttpContextData HttpContext { get; set; } = new();
}
