using Rewind.Core.Models;

namespace Rewind.Core.Archiving;

public class RewindArchiveContents
{
    public RewindManifest Manifest { get; set; } = new();
    public HttpContextData HttpContext { get; set; } = new();
    public string? Payload { get; set; }
    public string StackTrace { get; set; } = string.Empty;
}
