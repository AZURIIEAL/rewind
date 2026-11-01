using System.IO.Compression;
using Rewind.Core.Archiving;
using Rewind.Core.Masking;
using Rewind.Core.Models;

namespace Rewind.Core.Tests.Archiving;

public class ArchiveEdgeCaseTests : IDisposable
{
    private readonly string _tempDirectory =
        Path.Combine(Path.GetTempPath(), "rewind-edge-tests-" + Guid.NewGuid().ToString("N"));

    public ArchiveEdgeCaseTests() => Directory.CreateDirectory(_tempDirectory);

    private static RewindSnapshot BuildSnapshot(Exception? exception = null) => new()
    {
        Environment = "Production",
        ApplicationName = "Orders.Api",
        TraceId = "4bf92f3577b34da6a3ce929d0e0e4736",
        Exception = ExceptionData.FromException(exception ?? new InvalidOperationException("boom")),
        HttpContext = new HttpContextData
        {
            Method = "PUT",
            Path = "/orders/42",
            QueryString = "?expand=lines",
            RoutePattern = "/orders/{id}",
            RouteValues = new Dictionary<string, string> { ["id"] = "42" },
            Headers = new Dictionary<string, string> { ["Accept"] = "application/json" },
            BodyOmittedReason = "Content type 'application/pdf' is not captured."
        }
    };

    [Fact]
    public async Task RoundTripsEveryManifestAndContextField()
    {
        var snapshot = BuildSnapshot();
        var filePath = await new RewindArchiveWriter(_tempDirectory).WriteAsync(snapshot);

        var contents = await new RewindArchiveReader().ReadAsync(filePath);

        Assert.Equal(RewindManifest.CurrentFormatVersion, contents.Manifest.FormatVersion);
        Assert.Equal(snapshot.Timestamp, contents.Manifest.Timestamp);
        Assert.Equal("Orders.Api", contents.Manifest.ApplicationName);
        Assert.Equal("4bf92f3577b34da6a3ce929d0e0e4736", contents.Manifest.TraceId);
        Assert.Equal("boom", contents.Manifest.ExceptionMessage);
        Assert.Equal("PUT", contents.HttpContext.Method);
        Assert.Equal("?expand=lines", contents.HttpContext.QueryString);
        Assert.Equal("/orders/{id}", contents.HttpContext.RoutePattern);
        Assert.Equal("42", contents.HttpContext.RouteValues["id"]);
        Assert.Equal("Content type 'application/pdf' is not captured.", contents.HttpContext.BodyOmittedReason);
        Assert.Null(contents.Payload);
    }

    [Fact]
    public async Task WritesTheWholeInnerExceptionChain()
    {
        Exception exception;
        try
        {
            try
            {
                throw new FormatException("inner reason");
            }
            catch (Exception inner)
            {
                throw new InvalidOperationException("outer", inner);
            }
        }
        catch (Exception ex)
        {
            exception = ex;
        }

        var filePath = await new RewindArchiveWriter(_tempDirectory).WriteAsync(BuildSnapshot(exception));
        var contents = await new RewindArchiveReader().ReadAsync(filePath);

        Assert.Contains("System.InvalidOperationException: outer", contents.StackTrace);
        Assert.Contains("---> Inner Exception:", contents.StackTrace);
        Assert.Contains("System.FormatException: inner reason", contents.StackTrace);
        Assert.Contains(nameof(WritesTheWholeInnerExceptionChain), contents.StackTrace);
    }

    [Fact]
    public async Task ReadsArchivesWithMissingEntries_UsingDefaults()
    {
        var filePath = Path.Combine(_tempDirectory, "partial.rewind");
        using (var archive = ZipFile.Open(filePath, ZipArchiveMode.Create))
        {
            var entry = archive.CreateEntry("manifest.json");
            await using var writer = new StreamWriter(entry.Open());
            await writer.WriteAsync("""{"id":"abc","environment":"Staging"}""");
        }

        var contents = await new RewindArchiveReader().ReadAsync(filePath);

        Assert.Equal("abc", contents.Manifest.Id);
        Assert.Equal("Staging", contents.Manifest.Environment);
        Assert.Equal(1, contents.Manifest.FormatVersion);
        Assert.Equal(string.Empty, contents.HttpContext.Method);
        Assert.Null(contents.Payload);
        Assert.Equal(string.Empty, contents.StackTrace);
    }

    [Fact]
    public async Task WritesEntriesAsUtf8WithoutAByteOrderMark()
    {
        var snapshot = BuildSnapshot();
        snapshot.HttpContext.RawBodyPayload = """{"name":"Zoë"}""";
        var filePath = await new RewindArchiveWriter(_tempDirectory).WriteAsync(snapshot);

        using var archive = ZipFile.OpenRead(filePath);
        foreach (var entry in archive.Entries.Where(e => e.Length > 0))
        {
            using var stream = entry.Open();
            var firstBytes = new byte[3];
            _ = await stream.ReadAsync(firstBytes);
            Assert.False(firstBytes is [0xEF, 0xBB, 0xBF], $"{entry.Name} starts with a UTF-8 BOM");
        }

        using var payload = new StreamReader(archive.GetEntry("payload.json")!.Open());
        Assert.Equal("""{"name":"Zoë"}""", await payload.ReadToEndAsync());
    }

    [Fact]
    public async Task ReadsOlderArchivesThatHaveAByteOrderMark()
    {
        var filePath = Path.Combine(_tempDirectory, "old.rewind");
        using (var archive = ZipFile.Open(filePath, ZipArchiveMode.Create))
        {
            foreach (var (name, content) in new[] { ("manifest.json", """{"id":"old"}"""), ("payload.json", """{"a":1}""") })
            {
                await using var writer = new StreamWriter(archive.CreateEntry(name).Open(), new System.Text.UTF8Encoding(true));
                await writer.WriteAsync(content);
            }
        }

        var contents = await new RewindArchiveReader().ReadAsync(filePath);

        Assert.Equal("old", contents.Manifest.Id);
        Assert.Equal("""{"a":1}""", contents.Payload);
    }

    [Fact]
    public async Task ThrowsInvalidData_ForAFileThatIsNotAZip()
    {
        var filePath = Path.Combine(_tempDirectory, "corrupt.rewind");
        await File.WriteAllTextAsync(filePath, "definitely not a zip");

        await Assert.ThrowsAsync<InvalidDataException>(() => new RewindArchiveReader().ReadAsync(filePath));
    }

    [Fact]
    public async Task DoesNotLeaveFilesBehind_WhenWritingFails()
    {
        var writer = new RewindArchiveWriter(_tempDirectory);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => writer.WriteAsync(BuildSnapshot(), cancelled.Token));

        Assert.Empty(Directory.GetFiles(_tempDirectory));
    }

    [Fact]
    public async Task UsesTheInjectedMasker()
    {
        var writer = new RewindArchiveWriter(_tempDirectory, new JsonDataMasker(new MaskingOptions { EnableMasking = false }));
        var snapshot = BuildSnapshot();
        snapshot.HttpContext.Headers["Authorization"] = "Bearer real-token";

        var contents = await new RewindArchiveReader().ReadAsync(await writer.WriteAsync(snapshot));

        Assert.Equal("Bearer real-token", contents.HttpContext.Headers["Authorization"]);
    }

    [Fact]
    public async Task DoesNotModifyTheOriginalSnapshot()
    {
        var snapshot = BuildSnapshot();
        snapshot.HttpContext.Headers["Authorization"] = "Bearer real-token";
        snapshot.HttpContext.RawBodyPayload = """{"password":"hunter2"}""";

        await new RewindArchiveWriter(_tempDirectory).WriteAsync(snapshot);

        Assert.Equal("Bearer real-token", snapshot.HttpContext.Headers["Authorization"]);
        Assert.Equal("""{"password":"hunter2"}""", snapshot.HttpContext.RawBodyPayload);
    }

    [Fact]
    public async Task WritesASnapshotWithoutAnException()
    {
        var snapshot = BuildSnapshot();
        snapshot.Exception = null;

        var contents = await new RewindArchiveReader().ReadAsync(await new RewindArchiveWriter(_tempDirectory).WriteAsync(snapshot));

        Assert.Null(contents.Manifest.ExceptionType);
        Assert.Null(contents.Manifest.ExceptionMessage);
        Assert.Equal(string.Empty, contents.StackTrace);
        Assert.Equal("PUT", contents.HttpContext.Method);
    }

    [Fact]
    public async Task GivesEverySnapshotItsOwnFile()
    {
        var writer = new RewindArchiveWriter(_tempDirectory);

        var paths = await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => writer.WriteAsync(BuildSnapshot())));

        Assert.Equal(20, paths.Distinct().Count());
        Assert.Equal(20, Directory.GetFiles(_tempDirectory, "*.rewind").Length);
    }

    [Fact]
    public async Task MasksSecretsInInnerExceptionMessagesToo()
    {
        var exception = new InvalidOperationException(
            "Charge failed",
            new ArgumentException("Card 4111 1111 1111 1111 declined for jane.doe@example.com"));

        var filePath = await new RewindArchiveWriter(_tempDirectory).WriteAsync(BuildSnapshot(exception));
        var contents = await new RewindArchiveReader().ReadAsync(filePath);

        Assert.DoesNotContain("4111 1111 1111 1111", contents.StackTrace);
        Assert.DoesNotContain("jane.doe@", contents.StackTrace);
        Assert.Contains("4111-XXXX-XXXX-1111", contents.StackTrace);
        Assert.Contains("j***@example.com", contents.StackTrace);
    }

    [Fact]
    public async Task RewritingTheReadBackSnapshot_ProducesTheSameContent()
    {
        // What happens when a replayed request fails again: the already-masked data goes through the writer a second time.
        var snapshot = BuildSnapshot();
        snapshot.HttpContext.Headers["Authorization"] = "Bearer real-token-123";
        snapshot.HttpContext.Headers["Cookie"] = "session=abc; theme=dark";
        snapshot.HttpContext.QueryString = "?token=t0k3n&page=2";
        snapshot.HttpContext.RawBodyPayload = """{"cardNumber":"4111111111111111","ssn":"123-45-6789","password":"hunter2"}""";
        var writer = new RewindArchiveWriter(_tempDirectory);
        var first = await new RewindArchiveReader().ReadAsync(await writer.WriteAsync(snapshot));

        var again = BuildSnapshot();
        again.HttpContext.Headers = new Dictionary<string, string>(first.HttpContext.Headers);
        again.HttpContext.QueryString = first.HttpContext.QueryString;
        again.HttpContext.RawBodyPayload = first.Payload;
        var second = await new RewindArchiveReader().ReadAsync(await writer.WriteAsync(again));

        Assert.Equal(first.Payload, second.Payload);
        Assert.Equal(first.HttpContext.QueryString, second.HttpContext.QueryString);
        Assert.Equal(first.HttpContext.Headers, second.HttpContext.Headers);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDirectory))
        {
            Directory.Delete(_tempDirectory, recursive: true);
        }
    }
}
