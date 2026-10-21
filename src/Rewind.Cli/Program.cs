using Rewind.Cli.Commands;

if (args.Length == 0)
{
    PrintUsage();
    return 1;
}

var command = args[0];
var rest = args[1..];

try
{
    return command switch
    {
        "list" => await ListCommand.RunAsync(rest),
        "inspect" => await InspectCommand.RunAsync(rest),
        "replay" => await ReplayCommand.RunAsync(rest),
        "export-http" => await ExportHttpCommand.RunAsync(rest),
        "generate-test" => await GenerateTestCommand.RunAsync(rest),
        "-h" or "--help" or "help" => PrintUsageAndReturn(),
        _ => UnknownCommand(command)
    };
}
catch (Exception ex) when (ex is FileNotFoundException or InvalidDataException or HttpRequestException or IOException)
{
    Console.Error.WriteLine($"Error: {ex.Message}");
    return 1;
}

static int PrintUsageAndReturn()
{
    PrintUsage();
    return 0;
}

static int UnknownCommand(string unknownCommand)
{
    Console.Error.WriteLine($"Unknown command '{unknownCommand}'.");
    PrintUsage();
    return 1;
}

static void PrintUsage()
{
    Console.WriteLine("""
        Rewind CLI - inspect and replay .rewind snapshots

        Usage:
          rewind list [directory]
          rewind inspect <file.rewind>
          rewind replay <file.rewind> [--target <baseUrl>]
          rewind export-http <file.rewind> [--target <baseUrl>] [--output <path.http>]
          rewind generate-test <file.rewind> [--output <path.cs>] [--namespace <ns>] [--entry-point <type>]
        """);
}
