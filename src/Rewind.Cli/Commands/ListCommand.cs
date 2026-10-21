using Rewind.Core.Archiving;

namespace Rewind.Cli.Commands;

public static class ListCommand
{
    public static async Task<int> RunAsync(string[] args)
    {
        var (directory, _) = CliArgs.ParsePathAndOptions(args);
        directory ??= Directory.GetCurrentDirectory();

        if (!Directory.Exists(directory))
        {
            Console.Error.WriteLine($"Directory not found: {directory}");
            return 1;
        }

        var reader = new RewindArchiveReader();
        var rows = new List<(DateTimeOffset Timestamp, string Line)>();
        foreach (var file in Directory.GetFiles(directory, "*.rewind").Where(file => Path.GetExtension(file) == ".rewind"))
        {
            try
            {
                var contents = await reader.ReadAsync(file);
                var manifest = contents.Manifest;
                var request = $"{contents.HttpContext.Method} {contents.HttpContext.Path}";
                rows.Add((manifest.Timestamp,
                    $"{manifest.Timestamp.UtcDateTime:yyyy-MM-dd HH:mm:ss}  {Path.GetFileName(file),-40}  {request,-30}  {manifest.ExceptionType}: {manifest.ExceptionMessage}"));
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or System.Text.Json.JsonException)
            {
                Console.Error.WriteLine($"Skipping unreadable file {Path.GetFileName(file)}: {ex.Message}");
            }
        }

        if (rows.Count == 0)
        {
            Console.WriteLine($"No .rewind files in {directory}");
            return 0;
        }

        Console.WriteLine($"{"Captured (UTC)",-19}  {"File",-40}  {"Request",-30}  Exception");
        foreach (var row in rows.OrderByDescending(row => row.Timestamp))
        {
            Console.WriteLine(row.Line);
        }

        return 0;
    }
}
