using System.Reflection;
using Asp.Versioning;
using Asp.Versioning.Builder;
using Juvo.Api.Http;
using Juvo.Application.Features.System;
using Juvo.Application.Messaging;

namespace Juvo.Api.Endpoints;

internal static class SystemEndpoints
{
    /// <summary>The v1 route group every module maps its endpoints under: <c>/api/v1/...</c>.</summary>
    public static RouteGroupBuilder MapApiV1(this WebApplication app)
    {
        ApiVersionSet versionSet = app.NewApiVersionSet().HasApiVersion(new ApiVersion(1, 0)).ReportApiVersions().Build();
        return app.MapGroup("/api/v{version:apiVersion}").WithApiVersionSet(versionSet);
    }

    public static WebApplication MapSystemEndpoints(this WebApplication app)
    {
        var system = app.MapApiV1().MapGroup("/system").WithTags("System");

        system.MapGet("/info", () => Results.Ok(new SystemInfo(
                "Juvo BD API",
                typeof(SystemEndpoints).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "1.0.0",
                app.Environment.EnvironmentName)))
            .CacheOutput("Short")
            .WithName("GetSystemInfo")
            .WithSummary("Name, version and environment of the running API.");

        system.MapGet("/ping", async (string? name, IDispatcher dispatcher, CancellationToken cancellationToken) =>
            (await dispatcher.SendAsync(new PingQuery(name), cancellationToken)).ToHttpResult())
            .WithName("Ping")
            .WithSummary("Smoke test that runs a query through the full application pipeline.");

        return app;
    }

    internal sealed record SystemInfo(string Name, string Version, string Environment);
}
