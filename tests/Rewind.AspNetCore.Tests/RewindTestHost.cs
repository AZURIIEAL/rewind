using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Rewind.AspNetCore.Extensions;
using Rewind.AspNetCore.Options;
using Rewind.Core.Archiving;

namespace Rewind.AspNetCore.Tests;

// An in-memory app wired up with Rewind. An outer middleware records whatever exception escapes Rewind,
// so tests can check that the original exception is rethrown untouched.
public sealed class RewindTestHost : IAsyncDisposable
{
    private readonly IHost _host;

    private RewindTestHost(IHost host, string outputDirectory)
    {
        _host = host;
        OutputDirectory = outputDirectory;
    }

    public string OutputDirectory { get; }
    public List<Exception> EscapedExceptions { get; } = new();
    public HttpClient Client => _host.GetTestClient();

    public static async Task<RewindTestHost> StartAsync(Action<RewindOptions>? configure = null)
    {
        var outputDirectory = Path.Combine(Path.GetTempPath(), "rewind-aspnetcore-tests-" + Guid.NewGuid().ToString("N"));
        RewindTestHost? testHost = null;

        var host = new HostBuilder()
            .ConfigureWebHost(web => web
                .UseTestServer()
                .ConfigureServices(services =>
                {
                    services.AddRouting();
                    services.AddRewind(options =>
                    {
                        options.OutputDirectory = outputDirectory;
                        configure?.Invoke(options);
                    });
                })
                .Configure(app =>
                {
                    app.Use(async (context, next) =>
                    {
                        try
                        {
                            await next(context);
                        }
                        catch (Exception ex)
                        {
                            testHost!.EscapedExceptions.Add(ex);
                            context.Response.StatusCode = StatusCodes.Status500InternalServerError;
                        }
                    });
                    app.UseRewind();
                    app.UseRouting();
                    app.UseEndpoints(endpoints =>
                    {
                        endpoints.MapGet("/ok", () => "ok");
                        endpoints.MapPost("/orders/{orderId}", string (string orderId, OrderRequest request) =>
                            throw new InvalidOperationException($"Order {orderId} failed"));
                        endpoints.MapPost("/reset/{token}", string (string token) =>
                            throw new InvalidOperationException("Reset failed"));
                        endpoints.MapPost("/upload", string () =>
                            throw new InvalidOperationException("Upload failed"));
                        endpoints.MapGet("/throw/{kind}", string (string kind) => kind switch
                        {
                            "argument" => throw new ArgumentException("bad argument"),
                            _ => throw new InvalidOperationException("boom")
                        });
                    });
                }))
            .Build();

        testHost = new RewindTestHost(host, outputDirectory);
        await host.StartAsync();
        return testHost;
    }

    // Stopping the host drains the capture queue, so every snapshot is on disk once this returns.
    public async Task<List<RewindArchiveContents>> StopAndReadSnapshotsAsync()
    {
        await _host.StopAsync();

        if (!Directory.Exists(OutputDirectory))
        {
            return new List<RewindArchiveContents>();
        }

        var reader = new RewindArchiveReader();
        var snapshots = new List<RewindArchiveContents>();
        foreach (var file in Directory.GetFiles(OutputDirectory, "*.rewind"))
        {
            snapshots.Add(await reader.ReadAsync(file));
        }

        return snapshots;
    }

    public async ValueTask DisposeAsync()
    {
        _host.Dispose();
        if (Directory.Exists(OutputDirectory))
        {
            Directory.Delete(OutputDirectory, recursive: true);
        }

        await Task.CompletedTask;
    }

    public record OrderRequest(string Item, string Password);
}
