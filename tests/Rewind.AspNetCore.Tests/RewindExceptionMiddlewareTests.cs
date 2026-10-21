using System.Net;
using System.Text;

namespace Rewind.AspNetCore.Tests;

public class RewindExceptionMiddlewareTests
{
    private static StringContent Json(string json) => new(json, Encoding.UTF8, "application/json");

    [Fact]
    public async Task CapturesMaskedSnapshot_AndRethrowsOriginalException()
    {
        await using var host = await RewindTestHost.StartAsync();

        var response = await host.Client.PostAsync(
            "/orders/ORD-42?access_token=abc123secret",
            Json("""{"item":"book","password":"hunter2"}"""));
        var snapshots = await host.StopAndReadSnapshotsAsync();

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        var escaped = Assert.Single(host.EscapedExceptions);
        Assert.IsType<InvalidOperationException>(escaped);
        Assert.Equal("Order ORD-42 failed", escaped.Message);

        var snapshot = Assert.Single(snapshots);
        Assert.Equal("System.InvalidOperationException", snapshot.Manifest.ExceptionType);
        Assert.Equal("POST", snapshot.HttpContext.Method);
        Assert.Equal("/orders/ORD-42", snapshot.HttpContext.Path);
        Assert.Equal("/orders/{orderId}", snapshot.HttpContext.RoutePattern);
        Assert.Equal("ORD-42", snapshot.HttpContext.RouteValues["orderId"]);
        Assert.DoesNotContain("abc123secret", snapshot.HttpContext.QueryString);
        Assert.Contains("\"item\":\"book\"", snapshot.Payload);
        Assert.DoesNotContain("hunter2", snapshot.Payload);
        Assert.False(string.IsNullOrEmpty(snapshot.Manifest.TraceId));
    }

    [Fact]
    public async Task DoesNotCapture_WhenRequestSucceeds()
    {
        await using var host = await RewindTestHost.StartAsync();

        var response = await host.Client.GetAsync("/ok");
        var snapshots = await host.StopAndReadSnapshotsAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Empty(snapshots);
    }

    [Fact]
    public async Task DoesNotCapture_WhenDisabled()
    {
        await using var host = await RewindTestHost.StartAsync(options => options.Enabled = false);

        await host.Client.GetAsync("/throw/any");
        var snapshots = await host.StopAndReadSnapshotsAsync();

        Assert.Single(host.EscapedExceptions);
        Assert.Empty(snapshots);
    }

    [Fact]
    public async Task SkipsExceptions_RejectedByShouldCapture()
    {
        await using var host = await RewindTestHost.StartAsync(options =>
            options.ShouldCapture = (_, exception) => exception is not ArgumentException);

        await host.Client.GetAsync("/throw/argument");
        await host.Client.GetAsync("/throw/other");
        var snapshots = await host.StopAndReadSnapshotsAsync();

        var snapshot = Assert.Single(snapshots);
        Assert.Equal("System.InvalidOperationException", snapshot.Manifest.ExceptionType);
    }

    [Fact]
    public async Task RateLimitsRepeatsOfTheSameException()
    {
        await using var host = await RewindTestHost.StartAsync(options => options.MaxSnapshotsPerMinutePerException = 2);

        for (var i = 0; i < 5; i++)
        {
            await host.Client.GetAsync("/throw/other");
        }

        await host.Client.GetAsync("/throw/argument");
        var snapshots = await host.StopAndReadSnapshotsAsync();

        Assert.Equal(5, host.EscapedExceptions.Count(ex => ex is InvalidOperationException));
        Assert.Equal(2, snapshots.Count(s => s.Manifest.ExceptionType == "System.InvalidOperationException"));
        Assert.Single(snapshots, s => s.Manifest.ExceptionType == "System.ArgumentException");
    }

    [Fact]
    public async Task OmitsBody_WhenOverSizeLimit()
    {
        await using var host = await RewindTestHost.StartAsync(options => options.MaxBodySizeBytes = 16);

        await host.Client.PostAsync("/orders/1", Json("""{"item":"a much longer body than sixteen bytes","password":"x"}"""));
        var snapshots = await host.StopAndReadSnapshotsAsync();

        var snapshot = Assert.Single(snapshots);
        Assert.Null(snapshot.Payload);
        Assert.Contains("capture limit", snapshot.HttpContext.BodyOmittedReason);
    }

    [Fact]
    public async Task OmitsBody_ForBinaryContentTypes()
    {
        await using var host = await RewindTestHost.StartAsync();

        var content = new ByteArrayContent(new byte[] { 1, 2, 3, 4 });
        content.Headers.ContentType = new("application/octet-stream");
        await host.Client.PostAsync("/upload", content);
        var snapshots = await host.StopAndReadSnapshotsAsync();

        var snapshot = Assert.Single(snapshots);
        Assert.Null(snapshot.Payload);
        Assert.Contains("application/octet-stream", snapshot.HttpContext.BodyOmittedReason);
    }

    [Fact]
    public async Task MasksSensitiveRouteValuesInPath()
    {
        await using var host = await RewindTestHost.StartAsync();

        await host.Client.PostAsync("/reset/abc123secretvalue", null);
        var snapshots = await host.StopAndReadSnapshotsAsync();

        var snapshot = Assert.Single(snapshots);
        Assert.StartsWith("/reset/", snapshot.HttpContext.Path);
        Assert.DoesNotContain("abc123secretvalue", snapshot.HttpContext.Path);
        Assert.DoesNotContain("abc123secretvalue", snapshot.HttpContext.RouteValues["token"]);
    }

    [Fact]
    public async Task DeletesOldestSnapshots_BeyondMaxSnapshotFiles()
    {
        await using var host = await RewindTestHost.StartAsync(options =>
        {
            options.MaxSnapshotFiles = 2;
            options.MaxSnapshotsPerMinutePerException = 0;
        });

        for (var i = 0; i < 4; i++)
        {
            await host.Client.GetAsync("/throw/other");
        }

        var snapshots = await host.StopAndReadSnapshotsAsync();

        Assert.Equal(2, snapshots.Count);
    }
}
