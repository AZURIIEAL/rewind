using System.Diagnostics;
using System.Globalization;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Rewind.AspNetCore.Capture;
using Rewind.AspNetCore.Options;
using Rewind.Core.Models;

namespace Rewind.AspNetCore.Middleware;

public class RewindExceptionMiddleware
{
    private static readonly string[] CapturedContentTypeFragments =
    {
        "json", "xml", "text/", "application/x-www-form-urlencoded", "graphql"
    };

    private readonly RequestDelegate _next;
    private readonly RewindCaptureChannel _captureChannel;
    private readonly RewindCaptureRateLimiter _rateLimiter;
    private readonly RewindOptions _options;
    private readonly IHostEnvironment _environment;
    private readonly ILogger<RewindExceptionMiddleware> _logger;

    public RewindExceptionMiddleware(
        RequestDelegate next,
        RewindCaptureChannel captureChannel,
        RewindCaptureRateLimiter rateLimiter,
        RewindOptions options,
        IHostEnvironment environment,
        ILogger<RewindExceptionMiddleware> logger)
    {
        _next = next;
        _captureChannel = captureChannel;
        _rateLimiter = rateLimiter;
        _options = options;
        _environment = environment;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        if (!_options.Enabled)
        {
            await _next(context);
            return;
        }

        var bodyOmittedReason = PrepareBodyCapture(context);

        try
        {
            await _next(context);
        }
        catch (Exception ex)
        {
            await TryCaptureAsync(context, ex, bodyOmittedReason);
            throw;
        }
    }

    // Decides up front whether the body can be captured, and if so enables buffering so it can be re-read
    // after the app has consumed it. Nothing is actually read unless an exception occurs.
    // Returns why the body won't be captured, or null if it will be (or there is none).
    private string? PrepareBodyCapture(HttpContext context)
    {
        var request = context.Request;
        if (request.ContentLength == 0 || context.Features.Get<IHttpRequestBodyDetectionFeature>()?.CanHaveBody == false)
        {
            return null;
        }

        if (request.ContentLength > _options.MaxBodySizeBytes)
        {
            return $"Body is {request.ContentLength} bytes, over the {_options.MaxBodySizeBytes}-byte capture limit.";
        }

        if (!string.IsNullOrEmpty(request.ContentType) &&
            !CapturedContentTypeFragments.Any(fragment => request.ContentType.Contains(fragment, StringComparison.OrdinalIgnoreCase)))
        {
            return $"Content type '{request.ContentType}' is not captured.";
        }

        request.EnableBuffering();
        return null;
    }

    // Capturing must never change what the app does, so any failure here is logged and swallowed;
    // the caller always rethrows the original exception.
    private async Task TryCaptureAsync(HttpContext context, Exception exception, string? bodyOmittedReason)
    {
        try
        {
            if (exception is OperationCanceledException && context.RequestAborted.IsCancellationRequested)
            {
                return; // the client went away; not a bug worth replaying
            }

            if (_options.ShouldCapture is not null && !_options.ShouldCapture(context, exception))
            {
                return;
            }

            if (!_rateLimiter.TryAcquire(exception))
            {
                return;
            }

            string? body = null;
            if (bodyOmittedReason is null)
            {
                (body, bodyOmittedReason) = await ReadBufferedBodyAsync(context.Request);
            }

            _captureChannel.TryEnqueue(BuildSnapshot(context, exception, body, bodyOmittedReason));
        }
        catch (Exception captureError)
        {
            _logger.LogWarning(captureError, "Rewind failed to capture a snapshot for {ExceptionType}", exception.GetType().FullName);
        }
    }

    private async Task<(string? Body, string? OmittedReason)> ReadBufferedBodyAsync(HttpRequest request)
    {
        if (!request.Body.CanSeek)
        {
            return (null, null);
        }

        // Read one byte past the limit so a chunked body (no Content-Length) that's too large can be detected.
        var buffer = new byte[_options.MaxBodySizeBytes + 1];
        var total = 0;
        try
        {
            request.Body.Position = 0;
            int read;
            while (total < buffer.Length && (read = await request.Body.ReadAsync(buffer.AsMemory(total), request.HttpContext.RequestAborted)) > 0)
            {
                total += read;
            }

            // Rewind it again for any outer handler that re-executes the pipeline (e.g. UseExceptionHandler).
            request.Body.Position = 0;
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or BadHttpRequestException or ObjectDisposedException)
        {
            // A body we can't read (client gone, malformed upload) shouldn't cost us the rest of the snapshot.
            return (null, $"Body could not be read: {ex.Message}");
        }

        if (total > _options.MaxBodySizeBytes)
        {
            return (null, $"Body is over the {_options.MaxBodySizeBytes}-byte capture limit.");
        }

        return (total == 0 ? null : Encoding.UTF8.GetString(buffer, 0, total), null);
    }

    private RewindSnapshot BuildSnapshot(HttpContext context, Exception exception, string? rawBody, string? bodyOmittedReason)
    {
        var headers = new Dictionary<string, string>();
        foreach (var header in context.Request.Headers)
        {
            headers[header.Key] = header.Value.ToString();
        }

        var routeValues = new Dictionary<string, string>();
        foreach (var (key, value) in context.Request.RouteValues)
        {
            if (value is not null)
            {
                routeValues[key] = Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;
            }
        }

        return new RewindSnapshot
        {
            Environment = _environment.EnvironmentName,
            ApplicationName = _environment.ApplicationName,
            TraceId = Activity.Current?.TraceId.ToString() ?? context.TraceIdentifier,
            Exception = ExceptionData.FromException(exception),
            HttpContext = new HttpContextData
            {
                Method = context.Request.Method,
                Path = context.Request.Path.ToString(),
                QueryString = context.Request.QueryString.ToString(),
                RoutePattern = (context.GetEndpoint() as RouteEndpoint)?.RoutePattern.RawText,
                RouteValues = routeValues,
                Headers = headers,
                RawBodyPayload = rawBody,
                BodyOmittedReason = bodyOmittedReason
            }
        };
    }
}
