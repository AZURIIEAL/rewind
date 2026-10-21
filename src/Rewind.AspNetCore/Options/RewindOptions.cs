using Microsoft.AspNetCore.Http;
using Rewind.Core.Masking;

namespace Rewind.AspNetCore.Options;

public class RewindOptions
{
    // When false the middleware passes every request straight through and nothing is captured.
    public bool Enabled { get; set; } = true;

    public string OutputDirectory { get; set; } = Path.Combine(Directory.GetCurrentDirectory(), "App_Data", "rewind");
    public MaskingOptions Masking { get; set; } = new();

    // Request bodies larger than this are not captured; the snapshot records why instead.
    public int MaxBodySizeBytes { get; set; } = 64 * 1024;

    // Return false to skip capturing a given exception (e.g. expected validation failures).
    public Func<HttpContext, Exception, bool>? ShouldCapture { get; set; }

    // Caps snapshots of the same exception (type + throw site) per minute, so an exception storm
    // doesn't fill the disk with identical files. 0 or less disables the limit.
    public int MaxSnapshotsPerMinutePerException { get; set; } = 10;

    // Snapshots waiting to be written. When full, new snapshots are dropped rather than blocking requests.
    public int QueueCapacity { get; set; } = 100;

    // Oldest .rewind files beyond this count are deleted after each write. 0 or less keeps everything.
    public int MaxSnapshotFiles { get; set; } = 500;
}
