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

    [Fact]
    public async Task WriteAsync_MasksQueryStringRouteValuesPathAndExceptionMessage()
    {
        var writer = new RewindArchiveWriter(_tempDirectory);
        var snapshot = BuildSnapshot();
        snapshot.Exception = ExceptionData.FromException(new InvalidOperationException("No user with email john.doe@example.com"));
        snapshot.HttpContext.Path = "/reset/abc123secretvalue/users/123-45-6789";
        snapshot.HttpContext.QueryString = "?api_key=livekey999&page=2";
        snapshot.HttpContext.RouteValues = new Dictionary<string, string> { ["token"] = "abc123secretvalue", ["id"] = "123-45-6789" };

        var filePath = await writer.WriteAsync(snapshot);
        var contents = await new RewindArchiveReader().ReadAsync(filePath);

        Assert.DoesNotContain("abc123secretvalue", contents.HttpContext.Path);
        Assert.DoesNotContain("123-45-6789", contents.HttpContext.Path);
        Assert.StartsWith("/reset/", contents.HttpContext.Path);
        Assert.EndsWith("/users/XXX-XX-6789", contents.HttpContext.Path);
        Assert.NotEqual("abc123secretvalue", contents.HttpContext.RouteValues["token"]);
        Assert.DoesNotContain("livekey999", contents.HttpContext.QueryString);
        Assert.DoesNotContain("john.doe@example.com", contents.Manifest.ExceptionMessage);
        Assert.DoesNotContain("john.doe@example.com", contents.StackTrace);
    }

    [Fact]
    public async Task WriteAsync_MasksFormEncodedBodyByFieldName()
    {
        var writer = new RewindArchiveWriter(_tempDirectory);
        var snapshot = BuildSnapshot();
        snapshot.HttpContext.Headers["Content-Type"] = "application/x-www-form-urlencoded";
        snapshot.HttpContext.RawBodyPayload = "username=jdoe&password=hunter2";

        var filePath = await writer.WriteAsync(snapshot);
        var contents = await new RewindArchiveReader().ReadAsync(filePath);

        Assert.StartsWith("username=jdoe&password=", contents.Payload);
        Assert.DoesNotContain("hunter2", contents.Payload);
    }

    [Fact]
    public async Task WriteAsync_LeavesNoTempFileBehind()
    {
        var writer = new RewindArchiveWriter(_tempDirectory);

        await writer.WriteAsync(BuildSnapshot());

        Assert.Empty(Directory.GetFiles(_tempDirectory, "*.tmp"));
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
