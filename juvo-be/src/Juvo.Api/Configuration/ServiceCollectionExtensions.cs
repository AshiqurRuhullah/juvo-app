using System.IO.Compression;
using System.Threading.RateLimiting;
using Asp.Versioning;
using Juvo.Api.Http;
using Juvo.Application.Abstractions;
using Juvo.Infrastructure.Persistence;
using Juvo.Shared.Results;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.ResponseCompression;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Juvo.Api.Configuration;

internal static class ServiceCollectionExtensions
{
    public static IServiceCollection AddJuvoApi(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<CorsSettings>().Bind(configuration.GetSection(CorsSettings.SectionName)).ValidateDataAnnotations().ValidateOnStart();
        services.AddOptions<RateLimitSettings>().Bind(configuration.GetSection(RateLimitSettings.SectionName)).ValidateDataAnnotations().ValidateOnStart();
        services.AddOptions<OpenApiSettings>().Bind(configuration.GetSection(OpenApiSettings.SectionName)).ValidateOnStart();

        services.AddHttpContextAccessor();
        services.AddScoped<ICurrentUser, HttpCurrentUser>();
        services.AddScoped<IBranchContext, HttpBranchContext>();
        services.AddJuvoAuth();

        // Enums travel as text ("Shop", not 2), so the API is readable and stable.
        services.ConfigureHttpJsonOptions(options => options.SerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter()));

        services.AddJuvoProblemDetails();
        services.AddJuvoCors(configuration);
        services.AddJuvoRateLimiting(configuration);
        services.AddJuvoVersioningAndOpenApi();
        services.AddJuvoHealthChecks();

        services.AddResponseCompression(options =>
        {
            options.EnableForHttps = true;
            options.Providers.Add<BrotliCompressionProvider>();
            options.Providers.Add<GzipCompressionProvider>();
        });
        services.Configure<BrotliCompressionProviderOptions>(o => o.Level = CompressionLevel.Fastest);
        services.Configure<GzipCompressionProviderOptions>(o => o.Level = CompressionLevel.Fastest);

        services.AddOutputCache(options =>
            options.AddPolicy("Short", builder => builder.Expire(TimeSpan.FromSeconds(30))));

        return services;
    }

    private static void AddJuvoCors(this IServiceCollection services, IConfiguration configuration)
    {
        var settings = configuration.GetSection(CorsSettings.SectionName).Get<CorsSettings>() ?? new CorsSettings();
        services.AddCors(options => options.AddPolicy(CorsSettings.PolicyName, policy => policy
            .WithOrigins(settings.AllowedOrigins)
            .AllowAnyMethod()
            .WithHeaders("Authorization", "Content-Type", "Accept", "Accept-Language", "X-Branch-Id", "X-Correlation-Id", "Idempotency-Key")
            .WithExposedHeaders("X-Correlation-Id", "Retry-After")
            .AllowCredentials()));
    }

    private static void AddJuvoRateLimiting(this IServiceCollection services, IConfiguration configuration)
    {
        var settings = configuration.GetSection(RateLimitSettings.SectionName).Get<RateLimitSettings>() ?? new RateLimitSettings();

        services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

            static string ClientKey(HttpContext context) => context.Connection.RemoteIpAddress?.ToString() ?? "unknown";

            static FixedWindowRateLimiterOptions Window(int perMinute) => new()
            {
                PermitLimit = perMinute,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
                AutoReplenishment = true,
            };

            if (settings.Enabled)
            {
                options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
                    RateLimitPartition.GetFixedWindowLimiter("global:" + ClientKey(context), _ => Window(settings.GlobalPerMinute)));
            }

            // Named policies are applied to sensitive endpoints with .RequireRateLimiting("auth"|"otp"|"checkout").
            options.AddPolicy(RateLimitSettings.AuthPolicy, context => settings.Enabled
                ? RateLimitPartition.GetFixedWindowLimiter("auth:" + ClientKey(context), _ => Window(settings.AuthPerMinute))
                : RateLimitPartition.GetNoLimiter("auth"));
            options.AddPolicy(RateLimitSettings.OtpPolicy, context => settings.Enabled
                ? RateLimitPartition.GetFixedWindowLimiter("otp:" + ClientKey(context), _ => Window(settings.OtpPerMinute))
                : RateLimitPartition.GetNoLimiter("otp"));
            options.AddPolicy(RateLimitSettings.CheckoutPolicy, context => settings.Enabled
                ? RateLimitPartition.GetFixedWindowLimiter("checkout:" + ClientKey(context), _ => Window(settings.CheckoutPerMinute))
                : RateLimitPartition.GetNoLimiter("checkout"));

            options.AddPolicy(RateLimitSettings.StorefrontPolicy, context => settings.Enabled
                ? RateLimitPartition.GetFixedWindowLimiter("storefront:" + ClientKey(context), _ => Window(settings.StorefrontPerMinute))
                : RateLimitPartition.GetNoLimiter("storefront"));

            options.OnRejected = async (context, cancellationToken) =>
            {
                var http = context.HttpContext;
                if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
                {
                    http.Response.Headers.RetryAfter = ((int)Math.Ceiling(retryAfter.TotalSeconds)).ToString(System.Globalization.CultureInfo.InvariantCulture);
                }

                http.Response.StatusCode = StatusCodes.Status429TooManyRequests;
                var problems = http.RequestServices.GetRequiredService<IProblemDetailsService>();
                await problems.TryWriteAsync(new ProblemDetailsContext
                {
                    HttpContext = http,
                    ProblemDetails =
                    {
                        Status = StatusCodes.Status429TooManyRequests,
                        Title = "Too many requests. Please wait a moment and try again.",
                        Extensions = { ["code"] = ErrorCodes.General.TooManyRequests },
                    },
                });
            };
        });
    }

    private static void AddJuvoVersioningAndOpenApi(this IServiceCollection services)
    {
        services.AddApiVersioning(options =>
            {
                options.DefaultApiVersion = new ApiVersion(1, 0);
                options.AssumeDefaultVersionWhenUnspecified = false;
                options.ReportApiVersions = true;
                options.ApiVersionReader = new UrlSegmentApiVersionReader();
            })
            .AddApiExplorer(options =>
            {
                options.GroupNameFormat = "'v'V";
                options.SubstituteApiVersionInUrl = true;
            })
            .AddOpenApi(); // one OpenAPI document per API version (v1 at /openapi/v1.json)
    }

    private static void AddJuvoHealthChecks(this IServiceCollection services)
    {
        services.AddHealthChecks()
            .AddDbContextCheck<JuvoDbContext>("database", failureStatus: HealthStatus.Unhealthy, tags: ["ready"]);
    }
}
