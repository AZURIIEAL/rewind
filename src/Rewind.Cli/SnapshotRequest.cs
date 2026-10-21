using Rewind.Core.Archiving;
using Rewind.Core.Models;

namespace Rewind.Cli;

// Shared rules for turning a captured request back into one that can be sent again.
public static class SnapshotRequest
{
    private static readonly string[] HopByHopHeaders = { "host", "content-length", "transfer-encoding", "connection" };

    public static string PathAndQuery(HttpContextData http) => $"{http.Path}{http.QueryString}";

    public static string? ContentType(HttpContextData http) =>
        http.Headers.FirstOrDefault(header => string.Equals(header.Key, "Content-Type", StringComparison.OrdinalIgnoreCase)).Value;

    // Everything except hop-by-hop headers (recomputed by the client) and Content-Type, which belongs on the body.
    public static IEnumerable<KeyValuePair<string, string>> ForwardableHeaders(HttpContextData http) =>
        http.Headers.Where(header =>
            !HopByHopHeaders.Contains(header.Key, StringComparer.OrdinalIgnoreCase) &&
            !string.Equals(header.Key, "Content-Type", StringComparison.OrdinalIgnoreCase));

    public static void WarnIfBodyOmitted(RewindArchiveContents contents)
    {
        if (contents.HttpContext.BodyOmittedReason is { } reason)
        {
            Console.Error.WriteLine($"Warning: the original request body was not captured ({reason}) so it is not replayed.");
        }
    }
}
