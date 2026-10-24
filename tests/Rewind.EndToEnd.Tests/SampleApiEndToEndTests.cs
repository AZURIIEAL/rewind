using System.Net;
using System.Reflection;
using System.Runtime.Loader;
using System.Text;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.Extensions.DependencyInjection;
using Rewind.AspNetCore.Options;
using Rewind.Cli.Commands;
using Rewind.Core.Archiving;

namespace Rewind.EndToEnd.Tests;

// Runs the real sample API in memory and walks the whole Rewind loop:
// production-style failure -> masked .rewind file -> replay / generated test reproduces the same failure.
public class SampleApiEndToEndTests : IDisposable
{
    private const string SecretPayload = """{"orderId":"ORD-42","cardNumber":"4111111111111111","password":"hunter2"}""";

    private readonly string _outputDirectory =
        Path.Combine(Path.GetTempPath(), "rewind-e2e-" + Guid.NewGuid().ToString("N"));

    private WebApplicationFactory<Program> CreateFactory() =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            builder.ConfigureServices(services =>
                services.AddSingleton(new RewindOptions { OutputDirectory = _outputDirectory })));

    // Disposing the factory stops the host, which drains Rewind's queue onto disk.
    private async Task<RewindArchiveContents> CaptureBoomAsync()
    {
        await using (var factory = CreateFactory())
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "/boom?api_key=livekey999")
            {
                Content = new StringContent(SecretPayload, Encoding.UTF8, "application/json")
            };
            request.Headers.TryAddWithoutValidation("Authorization", "Bearer supersecrettoken123");
            request.Headers.TryAddWithoutValidation("Cookie", "session=sess777abc");

            var response = await factory.CreateClient().SendAsync(request);
            Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        }

        var file = Assert.Single(Directory.GetFiles(_outputDirectory, "*.rewind"));
        return await new RewindArchiveReader().ReadAsync(file);
    }

    [Fact]
    public async Task Capture_WritesAMaskedSnapshotOfTheFailingRequest()
    {
        var snapshot = await CaptureBoomAsync();
        var rawArchive = Encoding.UTF8.GetString(await File.ReadAllBytesAsync(Directory.GetFiles(_outputDirectory, "*.rewind")[0]));

        Assert.Equal("System.InvalidOperationException", snapshot.Manifest.ExceptionType);
        Assert.Equal("Simulated failure for order ORD-42", snapshot.Manifest.ExceptionMessage);
        Assert.Equal("Rewind.SampleApi", snapshot.Manifest.ApplicationName);
        Assert.Equal("POST", snapshot.HttpContext.Method);
        Assert.Equal("/boom", snapshot.HttpContext.Path);
        Assert.Equal("/boom", snapshot.HttpContext.RoutePattern);
        Assert.Contains("Program.cs", snapshot.StackTrace);

        Assert.Equal("""{"orderId":"ORD-42","cardNumber":"4111-XXXX-XXXX-1111","password":"P@ssw0rd-Replaced1!"}""", snapshot.Payload);
        Assert.StartsWith("Bearer ", snapshot.HttpContext.Headers["Authorization"]);
        Assert.StartsWith("session=", snapshot.HttpContext.Headers["Cookie"]);

        foreach (var secret in new[] { "livekey999", "supersecrettoken123", "sess777abc", "hunter2", "4111111111111111" })
        {
            Assert.DoesNotContain(secret, snapshot.HttpContext.QueryString);
            Assert.DoesNotContain(secret, string.Join("\n", snapshot.HttpContext.Headers.Values));
            Assert.DoesNotContain(secret, snapshot.Payload);
            Assert.DoesNotContain(secret, rawArchive); // entries are compressed, but check the raw bytes too
        }
    }

    [Fact]
    public async Task Replay_ReproducesTheSameException()
    {
        var original = await CaptureBoomAsync();
        File.Delete(Directory.GetFiles(_outputDirectory, "*.rewind")[0]);

        await using (var factory = CreateFactory())
        {
            using var request = ReplayCommand.BuildRequest(original, "http://localhost");
            var response = await factory.CreateClient().SendAsync(request);

            Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
            Assert.Contains("Simulated failure for order ORD-42", await response.Content.ReadAsStringAsync());
        }

        // The replayed request failed the same way, so Rewind captured it again.
        var replayed = await new RewindArchiveReader().ReadAsync(Assert.Single(Directory.GetFiles(_outputDirectory, "*.rewind")));
        Assert.Equal(original.Manifest.ExceptionType, replayed.Manifest.ExceptionType);
        Assert.Equal(original.Manifest.ExceptionMessage, replayed.Manifest.ExceptionMessage);
        Assert.Equal(original.HttpContext.Path, replayed.HttpContext.Path);
        Assert.Equal(original.Payload, replayed.Payload);
    }

    [Fact]
    public async Task GeneratedTest_CompilesWithoutImplicitUsings_AndFailsWithTheOriginalError()
    {
        var snapshot = await CaptureBoomAsync();
        var code = GenerateTestCommand.Build(snapshot, "SampleApi.ReplayTests", "Program");

        var assembly = Compile(code);
        var testClass = assembly.GetTypes().Single(type => type.Name.StartsWith("RewindReplay_"));
        var testMethod = testClass.GetMethods().Single(method => method.GetCustomAttribute<FactAttribute>() is not null);

        await using var factory = CreateFactory();
        var instance = Activator.CreateInstance(testClass, factory)!;

        var failure = await Assert.ThrowsAsync<Xunit.Sdk.TrueException>(() => (Task)testMethod.Invoke(instance, null)!);
        Assert.Contains("Replayed request failed with 500", failure.Message);
        Assert.Contains("Simulated failure for order ORD-42", failure.Message);
    }

    [Fact]
    public async Task GeneratedTest_AlsoCompilesCleanly_WithImplicitUsingsEnabled()
    {
        var code = GenerateTestCommand.Build(await CaptureBoomAsync(), "SampleApi.ReplayTests", "Program");

        // The same global usings the .NET SDK generates for a project with <ImplicitUsings>enable</ImplicitUsings>.
        const string implicitUsings = """
            global using System;
            global using System.Collections.Generic;
            global using System.IO;
            global using System.Linq;
            global using System.Net.Http;
            global using System.Threading;
            global using System.Threading.Tasks;
            """;

        Compile(code, implicitUsings);
    }

    [Fact]
    public async Task ExportHttp_WritesARequestTheSampleApiAccepts()
    {
        var snapshot = await CaptureBoomAsync();
        var http = ExportHttpCommand.Build(snapshot, "http://localhost:5057");

        Assert.Contains("POST {{baseUrl}}/boom?api_key=", http);
        Assert.Contains("Content-Type: application/json", http);
        Assert.Contains(snapshot.Payload!, http);
        Assert.DoesNotContain("hunter2", http);
    }

    [Fact]
    public async Task SuccessfulRequests_ProduceNoSnapshots()
    {
        await using (var factory = CreateFactory())
        {
            var response = await factory.CreateClient().GetAsync("/weatherforecast");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        Assert.False(Directory.Exists(_outputDirectory) && Directory.GetFiles(_outputDirectory).Length > 0);
    }

    // Compiles against exactly what the test process has loaded: the sample API, xunit and Mvc.Testing.
    private static Assembly Compile(string code, string? extraSource = null)
    {
        var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
            .Split(Path.PathSeparator)
            .Concat(AppDomain.CurrentDomain.GetAssemblies().Where(a => !a.IsDynamic && a.Location.Length > 0).Select(a => a.Location))
            .DistinctBy(Path.GetFileName)
            .Select(path => MetadataReference.CreateFromFile(path));

        var compilation = CSharpCompilation.Create(
            "GeneratedReplayTest_" + Guid.NewGuid().ToString("N"),
            new[] { code, extraSource }.OfType<string>().Select(source => CSharpSyntaxTree.ParseText(source)),
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));

        using var stream = new MemoryStream();
        var result = compilation.Emit(stream);
        var problems = result.Diagnostics.Where(d => d.Severity >= DiagnosticSeverity.Warning).Select(d => d.ToString()).ToList();
        Assert.True(result.Success && problems.Count == 0, "Generated test didn't compile cleanly:\n" + string.Join("\n", problems));

        stream.Position = 0;
        return AssemblyLoadContext.Default.LoadFromStream(stream);
    }

    public void Dispose()
    {
        if (Directory.Exists(_outputDirectory))
        {
            Directory.Delete(_outputDirectory, recursive: true);
        }
    }
}
