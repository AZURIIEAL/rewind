using Rewind.Core.Models;

namespace Rewind.Core.Archiving;

public interface IArchiveWriter
{
    Task<string> WriteAsync(RewindSnapshot snapshot, CancellationToken cancellationToken = default);
}
