using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Rewind.AspNetCore.Options;
using Rewind.Core.Archiving;

namespace Rewind.AspNetCore.Capture;

public class RewindCaptureBackgroundService : BackgroundService
{
    private readonly RewindCaptureChannel _channel;
    private readonly IArchiveWriter _archiveWriter;
    private readonly RewindOptions _options;
    private readonly ILogger<RewindCaptureBackgroundService> _logger;

    public RewindCaptureBackgroundService(
        RewindCaptureChannel channel,
        IArchiveWriter archiveWriter,
        RewindOptions options,
        ILogger<RewindCaptureBackgroundService> logger)
    {
        _channel = channel;
        _archiveWriter = archiveWriter;
        _options = options;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Reads until the channel is completed in StopAsync rather than until stoppingToken fires,
        // so snapshots queued just before shutdown are still written.
        await foreach (var snapshot in _channel.Reader.ReadAllAsync(CancellationToken.None))
        {
            try
            {
                var filePath = await _archiveWriter.WriteAsync(snapshot, CancellationToken.None);
                _logger.LogInformation("Rewind snapshot {SnapshotId} written to {FilePath}", snapshot.Id, filePath);
                EnforceRetention();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to write Rewind snapshot {SnapshotId}", snapshot.Id);
            }

            var dropped = _channel.TakeDroppedCount();
            if (dropped > 0)
            {
                _logger.LogWarning("Rewind dropped {DroppedCount} snapshot(s) because the capture queue was full", dropped);
            }
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _channel.Complete();
        await base.StopAsync(cancellationToken);
    }

    private void EnforceRetention()
    {
        if (_options.MaxSnapshotFiles <= 0 || !Directory.Exists(_options.OutputDirectory))
        {
            return;
        }

        var excess = new DirectoryInfo(_options.OutputDirectory)
            .GetFiles("*.rewind")
            .Where(file => file.Extension == ".rewind")
            .OrderByDescending(file => file.LastWriteTimeUtc)
            .Skip(_options.MaxSnapshotFiles);

        foreach (var file in excess)
        {
            try
            {
                file.Delete();
            }
            catch (IOException ex)
            {
                _logger.LogWarning(ex, "Rewind could not delete old snapshot {FilePath}", file.FullName);
            }
        }
    }
}
