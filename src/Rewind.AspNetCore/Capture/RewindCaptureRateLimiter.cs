using System.Collections.Concurrent;
using Rewind.AspNetCore.Options;

namespace Rewind.AspNetCore.Capture;

public class RewindCaptureRateLimiter
{
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(1);

    private readonly RewindOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly ConcurrentDictionary<string, WindowState> _windows = new();

    public RewindCaptureRateLimiter(RewindOptions options, TimeProvider timeProvider)
    {
        _options = options;
        _timeProvider = timeProvider;
    }

    public bool TryAcquire(Exception exception)
    {
        var limit = _options.MaxSnapshotsPerMinutePerException;
        if (limit <= 0)
        {
            return true;
        }

        var now = _timeProvider.GetUtcNow();
        var state = _windows.AddOrUpdate(
            BuildKey(exception),
            _ => new WindowState(now, 1),
            (_, current) => now - current.Start >= Window ? new WindowState(now, 1) : current with { Count = current.Count + 1 });

        return state.Count <= limit;
    }

    // Exception type plus the method that threw it identifies "the same bug" without depending on the
    // request path, which can be unbounded (/orders/1, /orders/2, ...) and would grow this dictionary forever.
    private static string BuildKey(Exception exception)
    {
        var site = exception.TargetSite;
        return $"{exception.GetType().FullName}|{site?.DeclaringType?.FullName}.{site?.Name}";
    }

    private sealed record WindowState(DateTimeOffset Start, int Count);
}
