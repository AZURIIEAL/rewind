using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.Extensions.Hosting;
using Rewind.Cli;
using Rewind.Cli.Commands;
using Rewind.Core.Archiving;
using Rewind.Core.Models;

namespace Rewind.Cli.Tests;

public class CommandTests
{
    private static RewindArchiveContents BuildContents(string? payload = """{"orderId":"ORD-42"}""") => new()
    {
        Manifest = new RewindManifest
        {
            Id = "0bc6004590d04bcd957f80562bcbdb41",
            Timestamp = new DateTimeOffset(2026, 10, 21, 12, 0, 0, TimeSpan.Zero),
            Environment = "Production",
            ExceptionType = "System.InvalidOperationException",
            ExceptionMessage = "Simulated failure"
        },
        HttpContext = new HttpContextData
        {
            Method = "POST",
            Path = "/orders/ORD-42",
            QueryString = "?page=2",
            Headers = new Dictionary<string, string>
            {
                ["Host"] = "prod.example.com",
                ["Content-Length"] = "20",
                ["Content-Type"] = "application/json; charset=utf-8",
                ["Authorization"] = "Bearer d4b2907e5c3a",
                ["X-Correlation-Id"] = "abc"
            }
        },
        Payload = payload
    };

    [Fact]
    public void CliArgs_ParsesPathAndOptions()
    {
        var (path, options) = CliArgs.ParsePathAndOptions(new[] { "file.rewind", "--target", "http://localhost:5057" });

        Assert.Equal("file.rewind", path);
        Assert.Equal("http://localhost:5057", options["target"]);
    }

    [Fact]
    public void CliArgs_AcceptsOptionsBeforeThePath_AndIgnoresOptionCase()
    {
        var (path, options) = CliArgs.ParsePathAndOptions(new[] { "--TARGET", "http://x", "file.rewind", "extra" });

        Assert.Equal("file.rewind", path);
        Assert.Equal("http://x", options["target"]);
    }

    [Fact]
    public void CliArgs_GivesATrailingOptionAnEmptyValue()
    {
        var (path, options) = CliArgs.ParsePathAndOptions(new[] { "file.rewind", "--out" });

        Assert.Equal("file.rewind", path);
        Assert.Equal(string.Empty, options["out"]);
    }

    [Fact]
    public void CliArgs_HandlesNoArguments()
    {
        var (path, options) = CliArgs.ParsePathAndOptions(Array.Empty<string>());

        Assert.Null(path);
        Assert.Empty(options);
    }

    [Fact]
    public async Task Replay_BuildRequest_ReproducesCapturedRequestAgainstTarget()
    {
        string? receivedBody = null;
        string? receivedContentType = null;
        string? receivedAuthorization = null;
        string? receivedHost = null;

        using var host = await new HostBuilder()
            .ConfigureWebHost(web => web.UseTestServer().Configure(app => app.Run(async context =>
            {
                receivedBody = await new StreamReader(context.Request.Body).ReadToEndAsync();
                receivedContentType = context.Request.ContentType;
                receivedAuthorization = context.Request.Headers.Authorization;
                receivedHost = context.Request.Host.Value;
                await context.Response.WriteAsync($"{context.Request.Method} {context.Request.Path}{context.Request.QueryString}");
            })))
            .StartAsync();

        using var request = ReplayCommand.BuildRequest(BuildContents(), "http://localhost/");
        using var response = await host.GetTestClient().SendAsync(request);

        Assert.Equal("POST /orders/ORD-42?page=2", await response.Content.ReadAsStringAsync());
        Assert.Equal("""{"orderId":"ORD-42"}""", receivedBody);
        Assert.Equal("application/json; charset=utf-8", receivedContentType);
        Assert.Equal("Bearer d4b2907e5c3a", receivedAuthorization);
        Assert.Equal("localhost", receivedHost);
    }

    [Fact]
    public void ExportHttp_WritesRequestLineHeadersAndBody()
    {
        var http = ExportHttpCommand.Build(BuildContents(), "http://localhost:5057");

        Assert.Contains("@baseUrl = http://localhost:5057", http);
        Assert.Contains("POST {{baseUrl}}/orders/ORD-42?page=2", http);
        Assert.Contains("Authorization: Bearer d4b2907e5c3a", http);
        Assert.Contains("Content-Type: application/json; charset=utf-8", http);
        Assert.DoesNotContain("Host:", http);
        Assert.DoesNotContain("Content-Length:", http);
        Assert.EndsWith("""{"orderId":"ORD-42"}""" + Environment.NewLine, http);
    }

    [Theory]
    [InlineData("""{"orderId":"ORD-42"}""")]
    [InlineData("{\n  \"note\": \"contains \\\"\\\"\\\" a raw-string delimiter\"\n}")]
    [InlineData(null)]
    public void GenerateTest_ProducesSyntacticallyValidCSharp(string? payload)
    {
        var code = GenerateTestCommand.Build(BuildContents(payload), "MyApi.Tests", "Program");

        var diagnostics = CSharpSyntaxTree.ParseText(code).GetDiagnostics()
            .Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);

        Assert.Empty(diagnostics);
        Assert.Contains("public class RewindReplay_0bc60045Tests : IClassFixture<WebApplicationFactory<Program>>", code);
        Assert.Contains("public async Task Replay_InvalidOperationException_DoesNotFail()", code);
        Assert.Contains("new HttpMethod(\"POST\"), \"/orders/ORD-42?page=2\"", code);
    }

    [Fact]
    public void GenerateTest_EmbedsPayloadVerbatim()
    {
        const string payload = "{\n  \"quote\": \"\\\"\\\"\\\"\"\n}";
        var code = GenerateTestCommand.Build(BuildContents(payload), "MyApi.Tests", "Program");

        var root = CSharpSyntaxTree.ParseText(code).GetRoot();
        var literal = root.DescendantTokens()
            .Single(token => token.IsKind(SyntaxKind.MultiLineRawStringLiteralToken));

        Assert.Equal(payload, literal.ValueText);
    }

    [Fact]
    public void SnapshotRequest_ForwardableHeaders_SkipsHopByHopAndContentType()
    {
        var headers = SnapshotRequest.ForwardableHeaders(BuildContents().HttpContext).Select(h => h.Key).ToList();

        Assert.Equal(new[] { "Authorization", "X-Correlation-Id" }, headers);
    }
}
