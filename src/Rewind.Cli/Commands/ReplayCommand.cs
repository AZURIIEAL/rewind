using System.Net.Http.Headers;
using Rewind.Core.Archiving;

namespace Rewind.Cli.Commands;

public static class ReplayCommand
{
    public static async Task<int> RunAsync(string[] args)
    {
        var (filePath, options) = CliArgs.ParsePathAndOptions(args);
        if (filePath is null)
        {
            Console.Error.WriteLine("Usage: rewind replay <file.rewind> [--target <baseUrl>]");
            return 1;
        }

        var target = options.GetValueOrDefault("target", "http://localhost:5000");

        var reader = new RewindArchiveReader();
        var contents = await reader.ReadAsync(filePath);
        SnapshotRequest.WarnIfBodyOmitted(contents);

        using var client = new HttpClient();
        using var request = BuildRequest(contents, target);

        Console.WriteLine($"Replaying {contents.HttpContext.Method} {SnapshotRequest.PathAndQuery(contents.HttpContext)} -> {target}");
        using var response = await client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        Console.WriteLine($"Status: {(int)response.StatusCode} {response.StatusCode}");
        Console.WriteLine("Body:");
        Console.WriteLine(body);

        return 0;
    }

    public static HttpRequestMessage BuildRequest(RewindArchiveContents contents, string target)
    {
        var http = contents.HttpContext;
        var request = new HttpRequestMessage(
            new HttpMethod(http.Method),
            $"{target.TrimEnd('/')}{SnapshotRequest.PathAndQuery(http)}");

        foreach (var (key, value) in SnapshotRequest.ForwardableHeaders(http))
        {
            request.Headers.TryAddWithoutValidation(key, value);
        }

        if (!string.IsNullOrEmpty(contents.Payload))
        {
            var content = new StringContent(contents.Payload);
            content.Headers.ContentType = MediaTypeHeaderValue.Parse(SnapshotRequest.ContentType(http) ?? "application/json");
            request.Content = content;
        }

        return request;
    }
}
