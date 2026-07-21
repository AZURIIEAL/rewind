using System.Threading.Channels;
using Rewind.Core.Models;

namespace Rewind.AspNetCore.Capture;

public class RewindCaptureChannel
{
    private readonly Channel<RewindSnapshot> _channel = Channel.CreateUnbounded<RewindSnapshot>(new UnboundedChannelOptions
    {
        SingleReader = true,
        SingleWriter = false
    });

    public ChannelReader<RewindSnapshot> Reader => _channel.Reader;

    public bool TryEnqueue(RewindSnapshot snapshot) => _channel.Writer.TryWrite(snapshot);
}
