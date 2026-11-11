using System.Text.Json;
using Juvo.Api.Configuration;
using Juvo.Api.Endpoints;
using Juvo.Api.Http;
using Juvo.Infrastructure.Identity;
using Juvo.Infrastructure.Options;
using Juvo.Infrastructure.Persistence;
using Juvo.Infrastructure.Storage;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Options;
using Scalar.AspNetCore;

namespace Juvo.Api.Configuration;

internal static class WebApplicationExtensions
{
    public static WebApplication UseJuvoApi(this WebApplication app)
    {
        // Correlation id first so every log line and error response carries it.
        app.UseMiddleware<CorrelationIdMiddleware>();
        app.UseExceptionHandler();
        app.UseStatusCodePages();

        if (!app.Environment.IsDevelopment())
        {
            app.UseHsts();
        }

        app.UseHttpsRedirection();
        app.UseMiddleware<SecurityHeadersMiddleware>();
        app.UseResponseCompression();

        UseUploadedFiles(app);

        app.UseCors(CorsSettings.PolicyName);
        app.UseRateLimiter();
        app.UseAuthentication();
        app.UseMiddleware<BranchContextMiddleware>(); // validates X-Branch-Id against the signed-in user, every request
        app.UseAuthorization();
        app.UseOutputCache();

        return app;
    }

    public static WebApplication MapJuvoEndpoints(this WebApplication app)
    {
        MapHealth(app);
        app.MapSystemEndpoints();
        app.MapAuthEndpoints();
        app.MapUmsEndpoints();
        app.MapCatalogEndpoints();
        app.MapInventoryEndpoints();
        app.MapStoreEndpoints();
        app.MapMeEndpoints();
        app.MapStoreAdminEndpoints();
        app.MapOrdersEndpoints();
        app.MapReturnsEndpoints();
        app.MapPaymentsEndpoints();
        app.MapBillingEndpoints();
        app.MapNotificationsEndpoints();

        if (app.Services.GetRequiredService<IOptions<OpenApiSettings>>().Value.Enabled)
        {
            app.MapOpenApi().WithDocumentPerVersion().AllowAnonymous();
            app.MapScalarApiReference(options => options.WithTitle("Juvo BD API")).AllowAnonymous();
        }

        return app;
    }

    /// <summary>Applies pending EF Core migrations. Used by <c>--migrate</c> and, in Development, at start-up.</summary>
    public static async Task MigrateDatabaseAsync(this WebApplication app)
    {
        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<JuvoDbContext>();
        var logger = scope.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger("Migrations");
        var pending = (await db.Database.GetPendingMigrationsAsync()).ToList();
        logger.LogInformation("Applying {Count} pending migration(s): {Names}", pending.Count, string.Join(", ", pending));
        await db.Database.MigrateAsync();

        // Reference data (permissions, roles, branches, first Super Admin) is seeded right after the schema is current.
        var seeder = scope.ServiceProvider.GetRequiredService<IdentitySeeder>();
        await seeder.SeedAsync();
        await scope.ServiceProvider.GetRequiredService<Juvo.Infrastructure.Catalog.CatalogSeeder>().SeedAsync();
        await scope.ServiceProvider.GetRequiredService<Juvo.Infrastructure.Geo.GeoSeeder>().SeedAsync();
        await scope.ServiceProvider.GetRequiredService<Juvo.Infrastructure.Billing.BillingSeeder>().SeedAsync();
        if (app.Configuration.GetValue<bool>("Seed:SampleCatalog"))
        {
            await scope.ServiceProvider.GetRequiredService<Juvo.Infrastructure.Catalog.SampleCatalogSeeder>().SeedAsync();
        }
    }

    private static void UseUploadedFiles(WebApplication app)
    {
        var storage = app.Services.GetRequiredService<IOptions<StorageOptions>>().Value;
        var root = StorageLocation.ResolveRoot(storage, app.Environment);
        Directory.CreateDirectory(root);

        app.UseStaticFiles(new StaticFileOptions
        {
            FileProvider = new PhysicalFileProvider(root),
            RequestPath = storage.PublicBaseUrl,
            ServeUnknownFileTypes = false,
            OnPrepareResponse = context =>
            {
                // Uploaded file names are unique (hash/guid), so they can be cached for a year.
                context.Context.Response.Headers.CacheControl = "public,max-age=31536000,immutable";
            },
        });
    }

    private static void MapHealth(WebApplication app)
    {
        // Liveness: the process is up. No dependency checks.
        app.MapHealthChecks("/health/live", new HealthCheckOptions
        {
            Predicate = _ => false,
            ResponseWriter = WriteHealth,
        }).AllowAnonymous();

        // Readiness: dependencies (database) are reachable.
        app.MapHealthChecks("/health/ready", new HealthCheckOptions
        {
            Predicate = check => check.Tags.Contains("ready"),
            ResponseWriter = WriteHealth,
        }).AllowAnonymous();
    }

    private static Task WriteHealth(HttpContext context, Microsoft.Extensions.Diagnostics.HealthChecks.HealthReport report)
    {
        context.Response.ContentType = "application/json";
        var body = new
        {
            status = report.Status.ToString(),
            totalDurationMs = (int)report.TotalDuration.TotalMilliseconds,
            checks = report.Entries.Select(e => new
            {
                name = e.Key,
                status = e.Value.Status.ToString(),
                durationMs = (int)e.Value.Duration.TotalMilliseconds,
            }),
        };
        return context.Response.WriteAsync(JsonSerializer.Serialize(body, JsonSerializerOptions.Web));
    }
}
