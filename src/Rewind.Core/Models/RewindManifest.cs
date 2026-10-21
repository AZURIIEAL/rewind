namespace Rewind.Core.Models;

public class RewindManifest
{
    public const int CurrentFormatVersion = 1;

    public int FormatVersion { get; set; } = CurrentFormatVersion;
    public string Id { get; set; } = string.Empty;
    public DateTimeOffset Timestamp { get; set; }
    public string Environment { get; set; } = string.Empty;
    public string? ApplicationName { get; set; }
    public string? TraceId { get; set; }
    public string? ExceptionType { get; set; }
    public string? ExceptionMessage { get; set; }
}
