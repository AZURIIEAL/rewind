namespace Rewind.Cli;

public static class CliArgs
{
    public static (string? Path, Dictionary<string, string> Options) ParsePathAndOptions(string[] args)
    {
        string? path = null;
        var options = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            if (arg.StartsWith("--", StringComparison.Ordinal))
            {
                var key = arg[2..];
                var value = i + 1 < args.Length ? args[++i] : string.Empty;
                options[key] = value;
            }
            else if (path is null)
            {
                path = arg;
            }
        }

        return (path, options);
    }
}
