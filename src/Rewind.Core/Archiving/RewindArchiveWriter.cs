using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Rewind.Core.Masking;
using Rewind.Core.Models;

namespace Rewind.Core.Archiving;

public class RewindArchiveWriter : IArchiveWriter
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private readonly string _outputDirectory;
    private readonly IMasker _masker;

    public RewindArchiveWriter(string outputDirectory, IMasker? masker = null)
    {
        _outputDirectory = outputDirectory;
        _masker = masker ?? new JsonDataMasker();
    }

    public async Task<string> WriteAsync(RewindSnapshot snapshot, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        Directory.CreateDirectory(_outputDirectory);
        var filePath = Path.Combine(_outputDirectory, $"{snapshot.Id}.rewind");

        var maskedHttpContext = MaskHttpContext(snapshot.HttpContext);

        await using var fileStream = new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.None);
        using var archive = new ZipArchive(fileStream, ZipArchiveMode.Create);

        await WriteEntryAsync(archive, "manifest.json", BuildManifest(snapshot), cancellationToken);
        await WriteEntryAsync(archive, "http_context.json", JsonSerializer.Serialize(maskedHttpContext, SerializerOptions), cancellationToken);
        await WriteEntryAsync(archive, "payload.json", maskedHttpContext.RawBodyPayload ?? string.Empty, cancellationToken);
        await WriteEntryAsync(archive, "stacktrace.txt", BuildStackTrace(snapshot.Exception), cancellationToken);

        return filePath;
    }

    private HttpContextData MaskHttpContext(HttpContextData httpContext)
    {
        return new HttpContextData
        {
            Method = httpContext.Method,
            Path = httpContext.Path,
            QueryString = httpContext.QueryString,
            Headers = new Dictionary<string, string>(_masker.MaskHeaders(httpContext.Headers)),
            RawBodyPayload = _masker.MaskJson(httpContext.RawBodyPayload)
        };
    }

    private static string BuildManifest(RewindSnapshot snapshot)
    {
        var manifest = new RewindManifest
        {
            Id = snapshot.Id,
            Timestamp = snapshot.Timestamp,
            Environment = snapshot.Environment,
            ExceptionType = snapshot.Exception?.Type,
            ExceptionMessage = snapshot.Exception?.Message
        };

        return JsonSerializer.Serialize(manifest, SerializerOptions);
    }

    private static string BuildStackTrace(ExceptionData? exception)
    {
        if (exception is null)
        {
            return string.Empty;
        }

        var builder = new StringBuilder();
        AppendException(builder, exception, depth: 0);
        return builder.ToString();
    }

    private static void AppendException(StringBuilder builder, ExceptionData exception, int depth)
    {
        var indent = new string(' ', depth * 2);
        builder.AppendLine($"{indent}{exception.Type}: {exception.Message}");
        if (!string.IsNullOrEmpty(exception.StackTrace))
        {
            builder.AppendLine(exception.StackTrace);
        }

        if (exception.InnerException is not null)
        {
            builder.AppendLine($"{indent}---> Inner Exception:");
            AppendException(builder, exception.InnerException, depth + 1);
        }
    }

    private static async Task WriteEntryAsync(ZipArchive archive, string entryName, string content, CancellationToken cancellationToken)
    {
        var entry = archive.CreateEntry(entryName, CompressionLevel.Optimal);
        await using var entryStream = entry.Open();
        await using var writer = new StreamWriter(entryStream, Encoding.UTF8);
        await writer.WriteAsync(content.AsMemory(), cancellationToken);
    }
}
