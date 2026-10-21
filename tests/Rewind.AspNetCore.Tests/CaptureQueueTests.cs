using Rewind.AspNetCore.Capture;
using Rewind.AspNetCore.Options;
using Rewind.Core.Models;

namespace Rewind.AspNetCore.Tests;

public class CaptureQueueTests
{
    [Fact]
    public void Channel_DropsAndCountsSnapshots_WhenFull()
    {
        var channel = new RewindCaptureChannel(new RewindOptions { QueueCapacity = 1 });

        Assert.True(channel.TryEnqueue(new RewindSnapshot()));
        Assert.False(channel.TryEnqueue(new RewindSnapshot()));
        Assert.False(channel.TryEnqueue(new RewindSnapshot()));

        Assert.Equal(2, channel.TakeDroppedCount());
        Assert.Equal(0, channel.TakeDroppedCount());
    }

    [Fact]
    public void RateLimiter_AllowsLimitPerMinute_ThenResetsAfterWindow()
    {
        var time = new ManualTimeProvider();
        var limiter = new RewindCaptureRateLimiter(new RewindOptions { MaxSnapshotsPerMinutePerException = 1 }, time);
        var exception = Thrown(() => throw new InvalidOperationException());

        Assert.True(limiter.TryAcquire(exception));
        Assert.False(limiter.TryAcquire(exception));

        time.Advance(TimeSpan.FromMinutes(1));

        Assert.True(limiter.TryAcquire(exception));
    }

    [Fact]
    public void RateLimiter_TracksDifferentExceptionTypesSeparately()
    {
        var limiter = new RewindCaptureRateLimiter(new RewindOptions { MaxSnapshotsPerMinutePerException = 1 }, new ManualTimeProvider());

        Assert.True(limiter.TryAcquire(Thrown(() => throw new InvalidOperationException())));
        Assert.True(limiter.TryAcquire(Thrown(() => throw new ArgumentException())));
    }

    private static Exception Thrown(Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            return ex;
        }

        throw new InvalidOperationException("Expected the action to throw.");
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }
}
