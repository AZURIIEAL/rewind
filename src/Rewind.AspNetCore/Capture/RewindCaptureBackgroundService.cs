using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Rewind.Core.Archiving;

namespace Rewind.AspNetCore.Capture;

public class RewindCaptureBackgroundService : BackgroundService
{
    private readonly RewindCaptureChannel _channel;
    private readonly IArchiveWriter _archiveWriter;
    private readonly ILogger<RewindCaptureBackgroundService> _logger;

    public RewindCaptureBackgroundService(
        RewindCaptureChannel channel,
        IArchiveWriter archiveWriter,
        ILogger<RewindCaptureBackgroundService> logger)
    {
        _channel = channel;
        _archiveWriter = archiveWriter;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var snapshot in _channel.Reader.ReadAllAsync(stoppingToken))
        {
            try
            {
                var filePath = await _archiveWriter.WriteAsync(snapshot, stoppingToken);
                _logger.LogInformation("Rewind snapshot {SnapshotId} written to {FilePath}", snapshot.Id, filePath);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Failed to write Rewind snapshot {SnapshotId}", snapshot.Id);
            }
        }
    }
}
