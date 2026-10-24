using System.Diagnostics;
using System.Net;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Rewind.AspNetCore.Extensions;
using Rewind.Core.Archiving;
using Rewind.Core.Models;

namespace Rewind.AspNetCore.Tests;

public class RewindPipelineEdgeCaseTests
{
    [Fact]
    public async Task ExceptionHandlerReExecution_CanStillReadTheRequestBody()
    {
        var outputDirectory = Path.Combine(Path.GetTempPath(), "rewind-handler-tests-" + Guid.NewGuid().ToString("N"));
        using var host = await new HostBuilder()
            .ConfigureWebHost(web => web
                .UseTestServer()
                .ConfigureServices(services =>
                {
                    services.AddRouting();
                    services.AddRewind(options => options.OutputDirectory = outputDirectory);
                })
                .Configure(app =>
                {
                    app.UseExceptionHandler("/error");
                    app.UseRewind();
                    app.UseRouting();
                    app.UseEndpoints(endpoints =>
                    {
                        endpoints.MapPost("/fail", async context =>
                        {
                            await new StreamReader(context.Request.Body).ReadToEndAsync();
                            throw new InvalidOperationException("fail");
                        });
                        endpoints.Map("/error", async context =>
                        {
                            var body = await new StreamReader(context.Request.Body).ReadToEndAsync();
                            context.Response.StatusCode = 500;
                            await context.Response.WriteAsync($"handled:{body}");
                        });
                    });
                }))
            .StartAsync();

        try
        {
            var response = await host.GetTestClient().PostAsync("/fail", new StringContent("""{"a":1}""", Encoding.UTF8, "application/json"));

            Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
            Assert.Equal("""handled:{"a":1}""", await response.Content.ReadAsStringAsync());

            await host.StopAsync();
            var snapshot = Assert.Single(Directory.GetFiles(outputDirectory, "*.rewind"));
            Assert.Equal("""{"a":1}""", (await new RewindArchiveReader().ReadAsync(snapshot)).Payload);
        }
        finally
        {
            if (Directory.Exists(outputDirectory))
            {
                Directory.Delete(outputDirectory, recursive: true);
            }
        }
    }

    [Fact]
    public async Task CapturesBodies_TheAppNeverRead()
    {
        await using var host = await RewindTestHost.StartAsync();

        await host.Client.PostAsync("/text", new StringContent("plain text body, email a@b.com", Encoding.UTF8, "text/plain"));
        var snapshot = Assert.Single(await host.StopAndReadSnapshotsAsync());

        Assert.Equal("plain text body, email a***@b.com", snapshot.Payload);
    }

    [Fact]
    public async Task CapturesChunkedBodies_WithoutContentLength()
    {
        await using var host = await RewindTestHost.StartAsync();

        var content = new StringContent("""{"item":"chunked","password":"x"}""", Encoding.UTF8, "application/json");
        var request = new HttpRequestMessage(HttpMethod.Post, "/orders/1") { Content = content };
        request.Headers.TransferEncodingChunked = true;
        await host.Client.SendAsync(request);
        var snapshot = Assert.Single(await host.StopAndReadSnapshotsAsync());

        Assert.Contains("\"item\":\"chunked\"", snapshot.Payload);
        Assert.Null(snapshot.HttpContext.BodyOmittedReason);
    }

    [Fact]
    public async Task OmitsChunkedBodies_OverTheLimit()
    {
        await using var host = await RewindTestHost.StartAsync(options => options.MaxBodySizeBytes = 8);

        var request = new HttpRequestMessage(HttpMethod.Post, "/text")
        {
            Content = new StringContent("this body is longer than eight bytes", Encoding.UTF8, "text/plain")
        };
        request.Headers.TransferEncodingChunked = true;
        await host.Client.SendAsync(request);
        var snapshot = Assert.Single(await host.StopAndReadSnapshotsAsync());

        Assert.Null(snapshot.Payload);
        Assert.Contains("capture limit", snapshot.HttpContext.BodyOmittedReason);
    }

    [Fact]
    public async Task RequestsWithoutBodies_HaveNoPayloadAndNoOmissionReason()
    {
        await using var host = await RewindTestHost.StartAsync();

        await host.Client.GetAsync("/throw/other?x=1");
        var snapshot = Assert.Single(await host.StopAndReadSnapshotsAsync());

        Assert.Null(snapshot.Payload);
        Assert.Null(snapshot.HttpContext.BodyOmittedReason);
        Assert.Equal("?x=1", snapshot.HttpContext.QueryString);
        Assert.Equal("other", snapshot.HttpContext.RouteValues["kind"]);
    }

    [Fact]
    public async Task CapturesInnerExceptions()
    {
        await using var host = await RewindTestHost.StartAsync();

        await host.Client.GetAsync("/inner");
        var snapshot = Assert.Single(await host.StopAndReadSnapshotsAsync());

        Assert.Contains("System.FormatException: inner cause", snapshot.StackTrace);
    }

    [Fact]
    public async Task CapturesExceptionsThrownAfterTheResponseStarted()
    {
        await using var host = await RewindTestHost.StartAsync();

        try
        {
            await host.Client.GetAsync("/late");
        }
        catch (Exception)
        {
            // TestServer may surface the broken response to the client; that's not what's under test.
        }

        var snapshot = Assert.Single(await host.StopAndReadSnapshotsAsync());
        Assert.Equal("Failed after response started", snapshot.Manifest.ExceptionMessage);
        Assert.Single(host.EscapedExceptions);
    }

    [Fact]
    public async Task IgnoresCancellations_CausedByTheClientGoingAway()
    {
        await using var host = await RewindTestHost.StartAsync();

        await host.Client.GetAsync("/abort");

        Assert.Single(host.EscapedExceptions);
        Assert.Empty(await host.StopAndReadSnapshotsAsync());
    }

    [Fact]
    public async Task CapturesCancellations_TheClientDidNotCause()
    {
        await using var host = await RewindTestHost.StartAsync();

        await host.Client.GetAsync("/timeout");
        var snapshot = Assert.Single(await host.StopAndReadSnapshotsAsync());

        Assert.Equal("System.Threading.Tasks.TaskCanceledException", snapshot.Manifest.ExceptionType);
    }

    [Fact]
    public async Task AFailingShouldCaptureFilter_StillRethrowsTheOriginalException()
    {
        await using var host = await RewindTestHost.StartAsync(options =>
            options.ShouldCapture = (_, _) => throw new NotSupportedException("buggy filter"));

        var response = await host.Client.GetAsync("/throw/other");
        var snapshots = await host.StopAndReadSnapshotsAsync();

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.IsType<InvalidOperationException>(Assert.Single(host.EscapedExceptions));
        Assert.Empty(snapshots);
    }

    [Fact]
    public async Task HandlesManyConcurrentFailures()
    {
        await using var host = await RewindTestHost.StartAsync(options =>
        {
            options.MaxSnapshotsPerMinutePerException = 0;
            options.QueueCapacity = 100;
        });

        await Task.WhenAll(Enumerable.Range(0, 50).Select(i =>
            host.Client.PostAsync($"/orders/{i}", new StringContent($$"""{"item":"n{{i}}","password":"p"}""", Encoding.UTF8, "application/json"))));
        var snapshots = await host.StopAndReadSnapshotsAsync();

        Assert.Equal(50, host.EscapedExceptions.Count);
        Assert.Equal(50, snapshots.Count);
        Assert.Equal(50, snapshots.Select(s => s.Manifest.Id).Distinct().Count());
        Assert.Equal(
            Enumerable.Range(0, 50).Select(i => i.ToString()).OrderBy(x => x),
            snapshots.Select(s => s.HttpContext.RouteValues["orderId"]).OrderBy(x => x));
        Assert.All(snapshots, s => Assert.Contains($"\"item\":\"n{s.HttpContext.RouteValues["orderId"]}\"", s.Payload));
    }

    [Fact]
    public async Task KeepsWriting_AfterAWriteFails()
    {
        var writer = new FlakyWriter(failuresBeforeSuccess: 1);
        await using var host = await RewindTestHost.StartAsync(
            options => options.MaxSnapshotsPerMinutePerException = 0,
            services => services.AddSingleton<IArchiveWriter>(writer));

        await host.Client.GetAsync("/throw/other");
        await host.Client.GetAsync("/throw/other");
        await host.StopAndReadSnapshotsAsync();

        Assert.Equal(2, writer.Attempts);
        Assert.Equal(1, writer.Successes);
    }

    [Fact]
    public async Task AFullQueue_DropsSnapshotsWithoutBlockingRequests()
    {
        var writer = new BlockingWriter();
        await using var host = await RewindTestHost.StartAsync(
            options =>
            {
                options.QueueCapacity = 1;
                options.MaxSnapshotsPerMinutePerException = 0;
            },
            services => services.AddSingleton<IArchiveWriter>(writer));

        var stopwatch = Stopwatch.StartNew();
        for (var i = 0; i < 5; i++)
        {
            var response = await host.Client.GetAsync("/throw/other");
            Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        }

        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(5), $"Requests took {stopwatch.Elapsed} while the writer was blocked.");

        writer.Release();
        await host.StopAndReadSnapshotsAsync();

        // One snapshot was being written (blocked) and one waited in the queue; the other three were dropped.
        Assert.Equal(2, writer.Written);
    }

    private sealed class FlakyWriter : IArchiveWriter
    {
        private int _failuresLeft;

        public FlakyWriter(int failuresBeforeSuccess) => _failuresLeft = failuresBeforeSuccess;

        public int Attempts { get; private set; }
        public int Successes { get; private set; }

        public Task<string> WriteAsync(RewindSnapshot snapshot, CancellationToken cancellationToken = default)
        {
            Attempts++;
            if (_failuresLeft-- > 0)
            {
                throw new IOException("disk full");
            }

            Successes++;
            return Task.FromResult("ok");
        }
    }

    private sealed class BlockingWriter : IArchiveWriter
    {
        private readonly TaskCompletionSource _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _written;

        public int Written => _written;

        public void Release() => _gate.TrySetResult();

        public async Task<string> WriteAsync(RewindSnapshot snapshot, CancellationToken cancellationToken = default)
        {
            await _gate.Task;
            Interlocked.Increment(ref _written);
            return "ok";
        }
    }
}
