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
        var tempPath = filePath + ".tmp";

        var maskedHttpContext = MaskHttpContext(snapshot.HttpContext);
        var maskedException = MaskException(snapshot.Exception);

        // Write to a temp file and rename, so a half-written snapshot never shows up as a .rewind file.
        try
        {
            await using (var fileStream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
            using (var archive = new ZipArchive(fileStream, ZipArchiveMode.Create))
            {
                await WriteEntryAsync(archive, "manifest.json", BuildManifest(snapshot, maskedException), cancellationToken);
                await WriteEntryAsync(archive, "http_context.json", JsonSerializer.Serialize(maskedHttpContext, SerializerOptions), cancellationToken);
                await WriteEntryAsync(archive, "payload.json", maskedHttpContext.RawBodyPayload ?? string.Empty, cancellationToken);
                await WriteEntryAsync(archive, "stacktrace.txt", BuildStackTrace(maskedException), cancellationToken);
            }

            File.Move(tempPath, filePath, overwrite: true);
        }
        catch
        {
            File.Delete(tempPath);
            throw;
        }

        return filePath;
    }

    private HttpContextData MaskHttpContext(HttpContextData httpContext)
    {
        var maskedRouteValues = _masker.MaskKeyValues(httpContext.RouteValues);

        return new HttpContextData
        {
            Method = httpContext.Method,
            Path = MaskPath(httpContext.Path, httpContext.RouteValues, maskedRouteValues),
            QueryString = _masker.MaskQueryString(httpContext.QueryString),
            RoutePattern = httpContext.RoutePattern,
            RouteValues = new Dictionary<string, string>(maskedRouteValues),
            Headers = new Dictionary<string, string>(_masker.MaskKeyValues(httpContext.Headers)),
            RawBodyPayload = MaskBody(httpContext),
            BodyOmittedReason = httpContext.BodyOmittedReason
        };
    }

    private string? MaskBody(HttpContextData httpContext)
    {
        var contentType = httpContext.Headers
            .FirstOrDefault(header => string.Equals(header.Key, "Content-Type", StringComparison.OrdinalIgnoreCase))
            .Value;

        if (httpContext.RawBodyPayload is not null &&
            contentType?.Contains("application/x-www-form-urlencoded", StringComparison.OrdinalIgnoreCase) == true)
        {
            return _masker.MaskQueryString(httpContext.RawBodyPayload);
        }

        return _masker.MaskJson(httpContext.RawBodyPayload);
    }

    // Masks the path one segment at a time: a segment holding a sensitive route value (e.g. /reset/{token})
    // gets that value's masked form, and every segment is also scanned for sensitive value patterns.
    private string MaskPath(string path, IDictionary<string, string> routeValues, IDictionary<string, string> maskedRouteValues)
    {
        var replacements = routeValues
            .Where(pair => pair.Value.Length > 0 && maskedRouteValues.TryGetValue(pair.Key, out var masked) && masked != pair.Value)
            .GroupBy(pair => pair.Value)
            .ToDictionary(group => group.Key, group => maskedRouteValues[group.First().Key]);

        var segments = path.Split('/');
        for (var i = 0; i < segments.Length; i++)
        {
            var decoded = Uri.UnescapeDataString(segments[i]);
            var masked = _masker.MaskText(replacements.GetValueOrDefault(decoded, decoded));
            if (masked != decoded)
            {
                segments[i] = Uri.EscapeDataString(masked);
            }
        }

        return string.Join('/', segments);
    }

    // Exception messages often echo user input ("No user with email ..."), so they get the same free-text masking.
    private ExceptionData? MaskException(ExceptionData? exception)
    {
        if (exception is null)
        {
            return null;
        }

        return new ExceptionData
        {
            Type = exception.Type,
            Message = _masker.MaskText(exception.Message),
            StackTrace = exception.StackTrace,
            InnerException = MaskException(exception.InnerException)
        };
    }

    private static string BuildManifest(RewindSnapshot snapshot, ExceptionData? exception)
    {
        var manifest = new RewindManifest
        {
            Id = snapshot.Id,
            Timestamp = snapshot.Timestamp,
            Environment = snapshot.Environment,
            ApplicationName = snapshot.ApplicationName,
            TraceId = snapshot.TraceId,
            ExceptionType = exception?.Type,
            ExceptionMessage = exception?.Message
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
        // No byte-order mark: payload.json etc. should be plain JSON for jq and other tools. (The reader still accepts one.)
        await using var writer = new StreamWriter(entryStream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        await writer.WriteAsync(content.AsMemory(), cancellationToken);
    }
}
