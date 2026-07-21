using System.IO.Compression;
using System.Text.Json;
using Rewind.Core.Models;

namespace Rewind.Core.Archiving;

public class RewindArchiveReader : IArchiveReader
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    public async Task<RewindArchiveContents> ReadAsync(string filePath, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(filePath))
        {
            throw new FileNotFoundException("Rewind archive not found.", filePath);
        }

        using var archive = ZipFile.OpenRead(filePath);

        var manifestJson = await ReadEntryAsync(archive, "manifest.json", cancellationToken);
        var httpContextJson = await ReadEntryAsync(archive, "http_context.json", cancellationToken);
        var payload = await ReadEntryAsync(archive, "payload.json", cancellationToken);
        var stackTrace = await ReadEntryAsync(archive, "stacktrace.txt", cancellationToken);

        return new RewindArchiveContents
        {
            Manifest = string.IsNullOrEmpty(manifestJson)
                ? new RewindManifest()
                : JsonSerializer.Deserialize<RewindManifest>(manifestJson, SerializerOptions) ?? new RewindManifest(),
            HttpContext = string.IsNullOrEmpty(httpContextJson)
                ? new HttpContextData()
                : JsonSerializer.Deserialize<HttpContextData>(httpContextJson, SerializerOptions) ?? new HttpContextData(),
            Payload = string.IsNullOrEmpty(payload) ? null : payload,
            StackTrace = stackTrace
        };
    }

    private static async Task<string> ReadEntryAsync(ZipArchive archive, string entryName, CancellationToken cancellationToken)
    {
        var entry = archive.GetEntry(entryName);
        if (entry is null)
        {
            return string.Empty;
        }

        using var stream = entry.Open();
        using var reader = new StreamReader(stream);
        return await reader.ReadToEndAsync(cancellationToken);
    }
}
