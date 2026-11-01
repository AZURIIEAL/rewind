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

    [Fact]
    public void RateLimiter_TreatsTheSameTypeFromDifferentThrowSitesAsDifferentBugs()
    {
        var limiter = new RewindCaptureRateLimiter(new RewindOptions { MaxSnapshotsPerMinutePerException = 1 }, new ManualTimeProvider());

        Assert.True(limiter.TryAcquire(Thrown(ThrowFromSiteA)));
        Assert.True(limiter.TryAcquire(Thrown(ThrowFromSiteB)));
        Assert.False(limiter.TryAcquire(Thrown(ThrowFromSiteA)));
    }

    [Fact]
    public void RateLimiter_CountsAWindowFromItsFirstSnapshot_NotFromTheClockMinute()
    {
        var time = new ManualTimeProvider();
        var limiter = new RewindCaptureRateLimiter(new RewindOptions { MaxSnapshotsPerMinutePerException = 1 }, time);
        var exception = Thrown(ThrowFromSiteA);

        Assert.True(limiter.TryAcquire(exception));
        time.Advance(TimeSpan.FromSeconds(59));
        Assert.False(limiter.TryAcquire(exception));
        time.Advance(TimeSpan.FromSeconds(1));
        Assert.True(limiter.TryAcquire(exception));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void RateLimiter_ZeroOrNegativeLimit_MeansUnlimited(int limit)
    {
        var limiter = new RewindCaptureRateLimiter(new RewindOptions { MaxSnapshotsPerMinutePerException = limit }, new ManualTimeProvider());
        var exception = Thrown(ThrowFromSiteA);

        Assert.All(Enumerable.Range(0, 1000), _ => Assert.True(limiter.TryAcquire(exception)));
    }

    [Fact]
    public void RateLimiter_AllowsExactlyTheLimit_UnderConcurrentRequests()
    {
        var limiter = new RewindCaptureRateLimiter(new RewindOptions { MaxSnapshotsPerMinutePerException = 10 }, new ManualTimeProvider());
        var exception = Thrown(ThrowFromSiteA);
        var allowed = 0;

        Parallel.For(0, 2000, _ =>
        {
            if (limiter.TryAcquire(exception))
            {
                Interlocked.Increment(ref allowed);
            }
        });

        Assert.Equal(10, allowed);
    }

    [Fact]
    public async Task Channel_HandsOutSnapshotsInOrder_AndFinishesOnceCompletedAndDrained()
    {
        var channel = new RewindCaptureChannel(new RewindOptions { QueueCapacity = 10 });
        var snapshots = Enumerable.Range(0, 3).Select(_ => new RewindSnapshot()).ToList();
        snapshots.ForEach(snapshot => channel.TryEnqueue(snapshot));

        channel.Complete();

        Assert.False(channel.TryEnqueue(new RewindSnapshot()));
        var read = new List<RewindSnapshot>();
        await foreach (var snapshot in channel.Reader.ReadAllAsync())
        {
            read.Add(snapshot);
        }

        Assert.Equal(snapshots, read);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void Channel_TreatsANonPositiveCapacityAsOne(int capacity)
    {
        var channel = new RewindCaptureChannel(new RewindOptions { QueueCapacity = capacity });

        Assert.True(channel.TryEnqueue(new RewindSnapshot()));
        Assert.False(channel.TryEnqueue(new RewindSnapshot()));
    }

    private static void ThrowFromSiteA() => throw new InvalidOperationException("a");

    private static void ThrowFromSiteB() => throw new InvalidOperationException("b");

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
