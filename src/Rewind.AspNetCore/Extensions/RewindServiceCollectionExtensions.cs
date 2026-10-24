using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Rewind.AspNetCore.Capture;
using Rewind.AspNetCore.Middleware;
using Rewind.AspNetCore.Options;
using Rewind.Core.Archiving;
using Rewind.Core.Masking;

namespace Rewind.AspNetCore.Extensions;

public static class RewindServiceCollectionExtensions
{
    public static IServiceCollection AddRewind(this IServiceCollection services, Action<RewindOptions>? configure = null)
    {
        RewindOptions options = new();
        configure?.Invoke(options);

        services.AddSingleton(options);
        services.TryAddSingleton(TimeProvider.System);
        // Resolved from RewindOptions (not captured here) so a later registration can replace either one.
        services.TryAddSingleton<IArchiveWriter>(serviceProvider =>
        {
            var resolved = serviceProvider.GetRequiredService<RewindOptions>();
            return new RewindArchiveWriter(resolved.OutputDirectory, new JsonDataMasker(resolved.Masking));
        });
        services.AddSingleton<RewindCaptureChannel>();
        services.AddSingleton<RewindCaptureRateLimiter>();
        services.AddHostedService<RewindCaptureBackgroundService>();

        return services;
    }

    public static IApplicationBuilder UseRewind(this IApplicationBuilder app)
    {
        return app.UseMiddleware<RewindExceptionMiddleware>();
    }
}
