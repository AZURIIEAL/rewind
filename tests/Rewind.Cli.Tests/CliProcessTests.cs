using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Rewind.Cli.Commands;
using Rewind.Core.Archiving;
using Rewind.Core.Models;

namespace Rewind.Cli.Tests;

// Runs the built rewind CLI as a separate process, exactly as a user would.
public class CliProcessTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "rewind-cli-tests-" + Guid.NewGuid().ToString("N"));

    public CliProcessTests() => Directory.CreateDirectory(_directory);

    private static async Task<(int ExitCode, string Output, string Error)> RunCliAsync(params string[] args)
    {
        var startInfo = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        startInfo.ArgumentList.Add(typeof(ReplayCommand).Assembly.Location);
        foreach (var arg in args)
        {
            startInfo.ArgumentList.Add(arg);
        }

        using var process = Process.Start(startInfo)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await process.WaitForExitAsync(timeout.Token);
        return (process.ExitCode, await output, await error);
    }

    private async Task<string> WriteSnapshotAsync(string body = """{"orderId":"ORD-42","password":"hunter2"}""")
    {
        var snapshot = new RewindSnapshot
        {
            Environment = "Production",
            ApplicationName = "Orders.Api",
            Exception = ExceptionData.FromException(new InvalidOperationException("Order ORD-42 failed")),
            HttpContext = new HttpContextData
            {
                Method = "POST",
                Path = "/orders/ORD-42",
                QueryString = "?token=secret999",
                RoutePattern = "/orders/{id}",
                RouteValues = new Dictionary<string, string> { ["id"] = "ORD-42" },
                Headers = new Dictionary<string, string>
                {
                    ["Content-Type"] = "application/json",
                    ["Authorization"] = "Bearer supersecret",
                    ["X-Tenant"] = "acme"
                },
                RawBodyPayload = body
            }
        };

        return await new RewindArchiveWriter(_directory).WriteAsync(snapshot);
    }

    [Fact]
    public async Task Help_PrintsUsageForEveryCommand()
    {
        var (exitCode, output, _) = await RunCliAsync("help");

        Assert.Equal(0, exitCode);
        foreach (var command in new[] { "list", "inspect", "replay", "export-http", "generate-test" })
        {
            Assert.Contains($"rewind {command}", output);
        }
    }

    [Theory]
    [InlineData(new string[0], "Usage:")]
    [InlineData(new[] { "frobnicate" }, "Unknown command 'frobnicate'")]
    [InlineData(new[] { "inspect" }, "Usage: rewind inspect")]
    [InlineData(new[] { "replay" }, "Usage: rewind replay")]
    [InlineData(new[] { "export-http" }, "Usage: rewind export-http")]
    [InlineData(new[] { "generate-test" }, "Usage: rewind generate-test")]
    public async Task BadInvocations_ExitWithOne(string[] args, string expectedMessage)
    {
        var (exitCode, output, error) = await RunCliAsync(args);

        Assert.Equal(1, exitCode);
        Assert.Contains(expectedMessage, output + error);
    }

    [Fact]
    public async Task Inspect_MissingFile_ReportsAnErrorInsteadOfCrashing()
    {
        var (exitCode, _, error) = await RunCliAsync("inspect", Path.Combine(_directory, "nope.rewind"));

        Assert.Equal(1, exitCode);
        Assert.StartsWith("Error: Rewind archive not found.", error);
        Assert.DoesNotContain("   at ", error);
    }

    [Fact]
    public async Task Inspect_CorruptFile_ReportsAnErrorInsteadOfCrashing()
    {
        var corrupt = Path.Combine(_directory, "corrupt.rewind");
        await File.WriteAllTextAsync(corrupt, "not a zip");

        var (exitCode, _, error) = await RunCliAsync("inspect", corrupt);

        Assert.Equal(1, exitCode);
        Assert.StartsWith("Error:", error);
    }

    [Fact]
    public async Task Inspect_PrintsTheMaskedSnapshot()
    {
        var file = await WriteSnapshotAsync();

        var (exitCode, output, _) = await RunCliAsync("inspect", file);

        Assert.Equal(0, exitCode);
        Assert.Contains("Exception:   System.InvalidOperationException: Order ORD-42 failed", output);
        Assert.Contains("Application: Orders.Api", output);
        Assert.Contains("Route:       /orders/{id}", output);
        Assert.Contains("{id} = ORD-42", output);
        Assert.Contains("X-Tenant: acme", output);
        Assert.Contains("\"orderId\":\"ORD-42\"", output);
        Assert.DoesNotContain("hunter2", output);
        Assert.DoesNotContain("supersecret", output);
        Assert.DoesNotContain("secret999", output);
    }

    [Fact]
    public async Task List_ShowsReadableSnapshots_AndSkipsCorruptOnes()
    {
        var file = await WriteSnapshotAsync();
        await File.WriteAllTextAsync(Path.Combine(_directory, "broken.rewind"), "not a zip");
        await File.WriteAllTextAsync(Path.Combine(_directory, "notes.txt"), "ignored");

        var (exitCode, output, error) = await RunCliAsync("list", _directory);

        Assert.Equal(0, exitCode);
        Assert.Contains(Path.GetFileName(file), output);
        Assert.Contains("POST /orders/ORD-42", output);
        Assert.Contains("Order ORD-42 failed", output);
        Assert.Contains("Skipping unreadable file broken.rewind", error);
        Assert.DoesNotContain("notes.txt", output);
    }

    [Fact]
    public async Task List_EmptyAndMissingDirectories()
    {
        var empty = await RunCliAsync("list", _directory);
        var missing = await RunCliAsync("list", Path.Combine(_directory, "missing"));

        Assert.Equal(0, empty.ExitCode);
        Assert.Contains("No .rewind files", empty.Output);
        Assert.Equal(1, missing.ExitCode);
        Assert.Contains("Directory not found", missing.Error);
    }

    [Fact]
    public async Task ExportHttp_And_GenerateTest_WriteFilesToTheRequestedPaths()
    {
        var file = await WriteSnapshotAsync();
        var httpPath = Path.Combine(_directory, "replay.http");
        var testPath = Path.Combine(_directory, "ReplayTests.cs");

        var export = await RunCliAsync("export-http", file, "--target", "http://localhost:5057", "--output", httpPath);
        var generate = await RunCliAsync("generate-test", file, "--output", testPath, "--namespace", "Orders.Tests");

        Assert.Equal(0, export.ExitCode);
        Assert.Contains("POST {{baseUrl}}/orders/ORD-42?token=", await File.ReadAllTextAsync(httpPath));
        Assert.Equal(0, generate.ExitCode);
        Assert.Contains("namespace Orders.Tests;", await File.ReadAllTextAsync(testPath));
    }

    [Fact]
    public async Task ExportHttp_DefaultsToAFileNextToTheSnapshot()
    {
        var file = await WriteSnapshotAsync();

        var (exitCode, _, _) = await RunCliAsync("export-http", file);

        Assert.Equal(0, exitCode);
        Assert.True(File.Exists(Path.ChangeExtension(file, ".http")));
    }

    [Fact]
    public async Task Replay_SendsTheMaskedRequestToARealServer()
    {
        var file = await WriteSnapshotAsync();
        var port = FreePort();
        using var listener = new HttpListener();
        listener.Prefixes.Add($"http://localhost:{port}/");
        listener.Start();

        var serverSide = Task.Run(async () =>
        {
            var context = await listener.GetContextAsync();
            using var reader = new StreamReader(context.Request.InputStream);
            var received = new
            {
                context.Request.HttpMethod,
                Url = context.Request.RawUrl,
                Body = await reader.ReadToEndAsync(),
                context.Request.ContentType,
                Authorization = context.Request.Headers["Authorization"],
                Tenant = context.Request.Headers["X-Tenant"]
            };
            context.Response.StatusCode = 500;
            await context.Response.OutputStream.WriteAsync(Encoding.UTF8.GetBytes("reproduced"));
            context.Response.Close();
            return received;
        });

        var (exitCode, output, _) = await RunCliAsync("replay", file, "--target", $"http://localhost:{port}");
        var request = await serverSide;

        Assert.Equal(0, exitCode);
        Assert.Contains("Status: 500 InternalServerError", output);
        Assert.Contains("reproduced", output);
        Assert.Equal("POST", request.HttpMethod);
        Assert.StartsWith("/orders/ORD-42?token=", request.Url);
        Assert.DoesNotContain("secret999", request.Url);
        Assert.Equal("""{"orderId":"ORD-42","password":"P@ssw0rd-Replaced1!"}""", request.Body);
        Assert.StartsWith("application/json", request.ContentType);
        Assert.StartsWith("Bearer ", request.Authorization);
        Assert.Equal("acme", request.Tenant);
    }

    [Fact]
    public async Task Replay_ToAServerThatIsntRunning_ReportsAnError()
    {
        var file = await WriteSnapshotAsync();

        var (exitCode, _, error) = await RunCliAsync("replay", file, "--target", $"http://localhost:{FreePort()}");

        Assert.Equal(1, exitCode);
        Assert.StartsWith("Error:", error);
    }

    [Fact]
    public async Task Replay_WarnsThatAnUncapturedBodyIsNotSent_ButStillReplays()
    {
        var snapshot = new RewindSnapshot
        {
            Exception = ExceptionData.FromException(new InvalidOperationException("upload failed")),
            HttpContext = new HttpContextData
            {
                Method = "PUT",
                Path = "/files/7",
                Headers = new Dictionary<string, string> { ["Content-Type"] = "application/pdf" },
                BodyOmittedReason = "Content type 'application/pdf' is not captured."
            }
        };
        var file = await new RewindArchiveWriter(_directory).WriteAsync(snapshot);
        var port = FreePort();
        using var listener = new HttpListener();
        listener.Prefixes.Add($"http://localhost:{port}/");
        listener.Start();

        var serverSide = Task.Run(async () =>
        {
            var context = await listener.GetContextAsync();
            using var reader = new StreamReader(context.Request.InputStream);
            var received = (context.Request.HttpMethod, context.Request.RawUrl, Body: await reader.ReadToEndAsync());
            context.Response.StatusCode = 200;
            context.Response.Close();
            return received;
        });

        var (exitCode, output, error) = await RunCliAsync("replay", file, "--target", $"http://localhost:{port}");
        var request = await serverSide;

        Assert.Equal(0, exitCode);
        Assert.Contains("Warning: the original request body was not captured (Content type 'application/pdf' is not captured.)", error);
        Assert.Contains("Status: 200 OK", output);
        Assert.Equal(("PUT", "/files/7", string.Empty), request);
    }

    [Fact]
    public async Task Inspect_WorksOnASnapshotWrittenByThisVersion_WithEveryOptionalFieldEmpty()
    {
        var file = await new RewindArchiveWriter(_directory).WriteAsync(new RewindSnapshot());

        var (exitCode, _, error) = await RunCliAsync("inspect", file);

        Assert.Equal(0, exitCode);
        Assert.Equal(string.Empty, error);
    }

    private static int FreePort()
    {
        using var socket = new TcpListener(IPAddress.Loopback, 0);
        socket.Start();
        return ((IPEndPoint)socket.LocalEndpoint).Port;
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }
}
