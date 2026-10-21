using System.Threading.Channels;
using Rewind.AspNetCore.Options;
using Rewind.Core.Models;

namespace Rewind.AspNetCore.Capture;

public class RewindCaptureChannel
{
    private readonly Channel<RewindSnapshot> _channel;
    private long _droppedCount;

    public RewindCaptureChannel(RewindOptions options)
    {
        // FullMode.Wait makes TryWrite return false when the queue is full instead of evicting anything,
        // so the first snapshots of a burst (usually the most useful) are the ones kept.
        _channel = Channel.CreateBounded<RewindSnapshot>(new BoundedChannelOptions(Math.Max(1, options.QueueCapacity))
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait
        });
    }

    public ChannelReader<RewindSnapshot> Reader => _channel.Reader;

    public bool TryEnqueue(RewindSnapshot snapshot)
    {
        if (_channel.Writer.TryWrite(snapshot))
        {
            return true;
        }

        Interlocked.Increment(ref _droppedCount);
        return false;
    }

    // Returns how many snapshots were dropped since the last call, and resets the count.
    public long TakeDroppedCount() => Interlocked.Exchange(ref _droppedCount, 0);

    public void Complete() => _channel.Writer.TryComplete();
}
