using System.Text;
using Rewind.Core.Archiving;

namespace Rewind.Cli.Commands;

public static class ExportHttpCommand
{
    public static async Task<int> RunAsync(string[] args)
    {
        var (filePath, options) = CliArgs.ParsePathAndOptions(args);
        if (filePath is null)
        {
            Console.Error.WriteLine("Usage: rewind export-http <file.rewind> [--target <baseUrl>] [--output <path.http>]");
            return 1;
        }

        var target = options.GetValueOrDefault("target", "http://localhost:5000");
        var outputPath = options.GetValueOrDefault("output", Path.ChangeExtension(filePath, ".http"));

        var reader = new RewindArchiveReader();
        var contents = await reader.ReadAsync(filePath);
        SnapshotRequest.WarnIfBodyOmitted(contents);

        await File.WriteAllTextAsync(outputPath, Build(contents, target));
        Console.WriteLine($"Wrote {outputPath}");
        Console.WriteLine("Open it in VS Code (REST Client extension) or Visual Studio 2022 (17.8+) and click 'Send Request'.");

        return 0;
    }

    public static string Build(RewindArchiveContents contents, string target)
    {
        var http = contents.HttpContext;
        var builder = new StringBuilder();
        builder.AppendLine($"@baseUrl = {target}");
        builder.AppendLine();
        builder.AppendLine($"### Rewind snapshot {contents.Manifest.Id} - {contents.Manifest.ExceptionType}: {contents.Manifest.ExceptionMessage}");
        builder.AppendLine($"### Captured {contents.Manifest.Timestamp:O} in {contents.Manifest.Environment}");
        builder.AppendLine($"{http.Method} {{{{baseUrl}}}}{SnapshotRequest.PathAndQuery(http)}");

        foreach (var (key, value) in SnapshotRequest.ForwardableHeaders(http))
        {
            builder.AppendLine($"{key}: {value}");
        }

        if (!string.IsNullOrEmpty(contents.Payload))
        {
            builder.AppendLine($"Content-Type: {SnapshotRequest.ContentType(http) ?? "application/json"}");
            builder.AppendLine();
            builder.AppendLine(contents.Payload);
        }

        return builder.ToString();
    }
}
