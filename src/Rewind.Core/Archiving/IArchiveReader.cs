namespace Rewind.Core.Archiving;

public interface IArchiveReader
{
    Task<RewindArchiveContents> ReadAsync(string filePath, CancellationToken cancellationToken = default);
}
