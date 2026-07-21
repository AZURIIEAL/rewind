using Rewind.Core.Archiving;
using Rewind.Core.Models;

namespace Rewind.Core.Tests.Archiving;

public class RewindArchiveReaderTests : IDisposable
{
    private readonly string _tempDirectory =
        Path.Combine(Path.GetTempPath(), "rewind-reader-tests-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task ReadAsync_RoundTripsWhatWriterWrote()
    {
        var writer = new RewindArchiveWriter(_tempDirectory);
        var snapshot = new RewindSnapshot
        {
            Environment = "Development",
            Exception = ExceptionData.FromException(new InvalidOperationException("boom")),
            HttpContext = new HttpContextData
            {
                Method = "POST",
                Path = "/api/orders",
                QueryString = "?id=1",
                Headers = new Dictionary<string, string> { ["Authorization"] = "Bearer supersecrettoken" },
                RawBodyPayload = """{"orderId":"ORD-42","password":"hunter2"}"""
            }
        };

        var filePath = await writer.WriteAsync(snapshot);

        var reader = new RewindArchiveReader();
        var contents = await reader.ReadAsync(filePath);

        Assert.Equal(snapshot.Id, contents.Manifest.Id);
        Assert.Equal(snapshot.Environment, contents.Manifest.Environment);
        Assert.Equal("System.InvalidOperationException", contents.Manifest.ExceptionType);
        Assert.Equal("POST", contents.HttpContext.Method);
        Assert.Equal("/api/orders", contents.HttpContext.Path);
        Assert.DoesNotContain("supersecrettoken", contents.HttpContext.Headers["Authorization"]);
        Assert.DoesNotContain("hunter2", contents.Payload);
        Assert.Contains("InvalidOperationException", contents.StackTrace);
    }

    [Fact]
    public async Task ReadAsync_ThrowsFileNotFound_WhenArchiveMissing()
    {
        var reader = new RewindArchiveReader();

        await Assert.ThrowsAsync<FileNotFoundException>(
            () => reader.ReadAsync(Path.Combine(_tempDirectory, "missing.rewind")));
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDirectory))
        {
            Directory.Delete(_tempDirectory, recursive: true);
        }
    }
}
