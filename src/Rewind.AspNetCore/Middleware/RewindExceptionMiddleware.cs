using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;
using Rewind.AspNetCore.Capture;
using Rewind.Core.Models;

namespace Rewind.AspNetCore.Middleware;

public class RewindExceptionMiddleware
{
    private readonly RequestDelegate _next;
    private readonly RewindCaptureChannel _captureChannel;
    private readonly IHostEnvironment _environment;

    public RewindExceptionMiddleware(RequestDelegate next, RewindCaptureChannel captureChannel, IHostEnvironment environment)
    {
        _next = next;
        _captureChannel = captureChannel;
        _environment = environment;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var rawBody = await ReadRequestBodyAsync(context.Request);

        try
        {
            await _next(context);
        }
        catch (Exception ex)
        {
            _captureChannel.TryEnqueue(BuildSnapshot(context, ex, rawBody));
            throw;
        }
    }

    private static async Task<string?> ReadRequestBodyAsync(HttpRequest request)
    {
        if (request.ContentLength is null or 0)
        {
            return null;
        }

        request.EnableBuffering();
        using var reader = new StreamReader(request.Body, leaveOpen: true);
        var body = await reader.ReadToEndAsync();
        request.Body.Position = 0;
        return body;
    }

    private RewindSnapshot BuildSnapshot(HttpContext context, Exception exception, string? rawBody)
    {
        var headers = new Dictionary<string, string>();
        foreach (var header in context.Request.Headers)
        {
            headers[header.Key] = header.Value.ToString();
        }

        return new RewindSnapshot
        {
            Environment = _environment.EnvironmentName,
            Exception = ExceptionData.FromException(exception),
            HttpContext = new HttpContextData
            {
                Method = context.Request.Method,
                Path = context.Request.Path.ToString(),
                QueryString = context.Request.QueryString.ToString(),
                Headers = headers,
                RawBodyPayload = rawBody
            }
        };
    }
}
