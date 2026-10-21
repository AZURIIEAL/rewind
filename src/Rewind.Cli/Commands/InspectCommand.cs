using Rewind.Core.Archiving;

namespace Rewind.Cli.Commands;

public static class InspectCommand
{
    public static async Task<int> RunAsync(string[] args)
    {
        var (filePath, _) = CliArgs.ParsePathAndOptions(args);
        if (filePath is null)
        {
            Console.Error.WriteLine("Usage: rewind inspect <file.rewind>");
            return 1;
        }

        var reader = new RewindArchiveReader();
        var contents = await reader.ReadAsync(filePath);

        Console.WriteLine($"Id:          {contents.Manifest.Id}");
        Console.WriteLine($"Timestamp:   {contents.Manifest.Timestamp:O}");
        Console.WriteLine($"Environment: {contents.Manifest.Environment}");
        Console.WriteLine($"Application: {contents.Manifest.ApplicationName ?? "(unknown)"}");
        Console.WriteLine($"Trace Id:    {contents.Manifest.TraceId ?? "(none)"}");
        Console.WriteLine($"Exception:   {contents.Manifest.ExceptionType}: {contents.Manifest.ExceptionMessage}");
        Console.WriteLine();
        Console.WriteLine($"Request:     {contents.HttpContext.Method} {SnapshotRequest.PathAndQuery(contents.HttpContext)}");
        if (contents.HttpContext.RoutePattern is not null)
        {
            Console.WriteLine($"Route:       {contents.HttpContext.RoutePattern}");
        }

        foreach (var routeValue in contents.HttpContext.RouteValues)
        {
            Console.WriteLine($"  {{{routeValue.Key}}} = {routeValue.Value}");
        }

        Console.WriteLine("Headers:");
        foreach (var header in contents.HttpContext.Headers)
        {
            Console.WriteLine($"  {header.Key}: {header.Value}");
        }

        Console.WriteLine();
        Console.WriteLine("Payload:");
        Console.WriteLine(contents.Payload
                          ?? (contents.HttpContext.BodyOmittedReason is { } reason ? $"(not captured: {reason})" : "(none)"));
        Console.WriteLine();
        Console.WriteLine("Stack Trace:");
        Console.WriteLine(contents.StackTrace);

        return 0;
    }
}
