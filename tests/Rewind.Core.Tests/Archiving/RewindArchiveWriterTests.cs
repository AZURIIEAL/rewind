using System.IO.Compression;
using Rewind.Core.Archiving;
using Rewind.Core.Models;

namespace Rewind.Core.Tests.Archiving;

public class RewindArchiveWriterTests : IDisposable
{
    private readonly string _tempDirectory =
        Path.Combine(Path.GetTempPath(), "rewind-tests-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task WriteAsync_CreatesRewindFileWithExpectedEntries()
    {
        var writer = new RewindArchiveWriter(_tempDirectory);
        var snapshot = BuildSnapshot();

        var filePath = await writer.WriteAsync(snapshot);

        Assert.True(File.Exists(filePath));
        Assert.EndsWith(".rewind", filePath);

        using var archive = ZipFile.OpenRead(filePath);
        var entryNames = archive.Entries.Select(e => e.Name).ToArray();

        Assert.Contains("manifest.json", entryNames);
        Assert.Contains("http_context.json", entryNames);
        Assert.Contains("payload.json", entryNames);
        Assert.Contains("stacktrace.txt", entryNames);
    }

    [Fact]
    public async Task WriteAsync_MasksSensitiveDataBeforeWriting()
    {
        var writer = new RewindArchiveWriter(_tempDirectory);
        var snapshot = BuildSnapshot();
        snapshot.HttpContext.RawBodyPayload = """{"password":"hunter2"}""";
        snapshot.HttpContext.Headers["Authorization"] = "Bearer supersecrettoken";

        var filePath = await writer.WriteAsync(snapshot);

        using var archive = ZipFile.OpenRead(filePath);

        var payloadEntry = archive.GetEntry("payload.json")!;
        await using (var payloadStream = payloadEntry.Open())
        using (var reader = new StreamReader(payloadStream))
        {
            var payload = await reader.ReadToEndAsync();
            Assert.DoesNotContain("hunter2", payload);
        }

        var contextEntry = archive.GetEntry("http_context.json")!;
        await using (var contextStream = contextEntry.Open())
        using (var reader = new StreamReader(contextStream))
        {
            var context = await reader.ReadToEndAsync();
            Assert.DoesNotContain("supersecrettoken", context);
        }
    }

    private static RewindSnapshot BuildSnapshot()
    {
        return new RewindSnapshot
        {
            Environment = "Development",
            Exception = ExceptionData.FromException(new InvalidOperationException("boom")),
            HttpContext = new HttpContextData
            {
                Method = "POST",
                Path = "/api/orders",
                QueryString = "?id=1",
                Headers = new Dictionary<string, string>(),
                RawBodyPayload = """{"orderId":1}"""
            }
        };
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDirectory))
        {
            Directory.Delete(_tempDirectory, recursive: true);
        }
    }
}
