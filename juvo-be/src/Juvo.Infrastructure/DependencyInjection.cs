using Juvo.Application.Abstractions;
using Juvo.Application.Identity;
using Juvo.Domain.Identity;
using Juvo.Infrastructure.Identity;
using Juvo.Infrastructure.Options;
using Juvo.Infrastructure.Outbox;
using Juvo.Infrastructure.Persistence;
using Juvo.Infrastructure.Persistence.Interceptors;
using Juvo.Infrastructure.Storage;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Juvo.Infrastructure;

public static class DependencyInjection
{
    public const string ConnectionStringName = "Default";

    /// <summary>Registers the database, identity, interceptors, unit of work, outbox, file storage and validated options.</summary>
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration configuration, IHostEnvironment environment)
    {
        services.AddOptions<StorageOptions>().Bind(configuration.GetSection(StorageOptions.SectionName))
            .ValidateDataAnnotations().ValidateOnStart();
        services.AddOptions<OutboxOptions>().Bind(configuration.GetSection(OutboxOptions.SectionName))
            .ValidateDataAnnotations().ValidateOnStart();
        services.AddOptions<DatabaseOptions>().Bind(configuration.GetSection(DatabaseOptions.SectionName))
            .ValidateDataAnnotations().ValidateOnStart();
        services.AddOptions<AuthOptions>().Bind(configuration.GetSection(AuthOptions.SectionName))
            .ValidateDataAnnotations().ValidateOnStart();
        services.AddOptions<NotificationOptions>().Bind(configuration.GetSection(NotificationOptions.SectionName))
            .ValidateDataAnnotations().ValidateOnStart();

        var connectionString = configuration.GetConnectionString(ConnectionStringName);
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException(
                $"Connection string 'ConnectionStrings:{ConnectionStringName}' is missing. Run scripts/setup-db.ps1 and store it " +
                "with: dotnet user-secrets set \"ConnectionStrings:Default\" \"<value>\" --project juvo-be/src/Juvo.Api");
        }

        var compatibilityLevel = configuration.GetSection(DatabaseOptions.SectionName).GetValue<int?>(nameof(DatabaseOptions.CompatibilityLevel)) ?? 150;

        services.TryAddSingleton(TimeProvider.System);
        services.AddScoped<BranchWriteGuardInterceptor>();
        services.AddScoped<AuditSaveChangesInterceptor>();
        services.AddScoped<DomainEventsOutboxInterceptor>();

        services.AddDbContext<JuvoDbContext>((provider, builder) =>
        {
            builder.UseSqlServer(connectionString, sql =>
            {
                sql.EnableRetryOnFailure(maxRetryCount: 5, maxRetryDelay: TimeSpan.FromSeconds(10), errorNumbersToAdd: null);
                sql.MigrationsAssembly(typeof(JuvoDbContext).Assembly.GetName().Name);
                sql.UseCompatibilityLevel(compatibilityLevel);
            });

            // Order matters: refuse writes into other branches first, then domain events become outbox rows, then stamp and audit.
            builder.AddInterceptors(
                provider.GetRequiredService<BranchWriteGuardInterceptor>(),
                provider.GetRequiredService<DomainEventsOutboxInterceptor>(),
                provider.GetRequiredService<AuditSaveChangesInterceptor>());
        });
        services.AddScoped<IApplicationDbContext>(provider => provider.GetRequiredService<JuvoDbContext>());

        services.AddScoped<IUnitOfWork, UnitOfWork>();
        services.AddScoped<IOutboxWriter, OutboxWriter>();
        services.AddSingleton<IFileStorage, LocalFileStorage>();
        services.AddSingleton<IImageProcessor, SkiaImageProcessor>();
        services.AddHostedService<OutboxProcessor>();

        AddIdentity(services, configuration, environment);

        services.AddSingleton<SigningKeyProvider>();
        services.AddSingleton<IAccessTokenService, AccessTokenService>();
        services.AddScoped<ISmsSender, SandboxSmsSender>();
        services.AddScoped<IEmailSender, SandboxEmailSender>();
        services.AddScoped<IAccessDeniedAuditor, AccessDeniedAuditor>();
        services.AddScoped<IdentitySeeder>();
        services.AddScoped<Catalog.CatalogSeeder>();

        return services;
    }

    private static void AddIdentity(IServiceCollection services, IConfiguration configuration, IHostEnvironment environment)
    {
        // Reset tokens and the authenticator key use data protection. Keys are kept on disk so they survive restarts.
        var keysPath = configuration["DataProtection:KeysPath"];
        var keysDirectory = Path.GetFullPath(string.IsNullOrWhiteSpace(keysPath)
            ? Path.Combine(environment.ContentRootPath, "App_Data", "keys")
            : keysPath);
        services.AddDataProtection()
            .SetApplicationName("JuvoBD")
            .PersistKeysToFileSystem(new DirectoryInfo(keysDirectory));

        services.AddIdentityCore<StaffUser>()
            .AddEntityFrameworkStores<JuvoDbContext>()
            .AddDefaultTokenProviders();

        services.AddOptions<IdentityOptions>().Configure<IOptions<AuthOptions>>((identity, auth) =>
        {
            var settings = auth.Value;
            identity.Password.RequiredLength = settings.PasswordMinLength;
            identity.Password.RequireDigit = true;
            identity.Password.RequireLowercase = true;
            identity.Password.RequireUppercase = true;
            identity.Password.RequireNonAlphanumeric = true;
            identity.Password.RequiredUniqueChars = 4;
            identity.Lockout.AllowedForNewUsers = true;
            identity.Lockout.MaxFailedAccessAttempts = settings.MaxFailedAccessAttempts;
            identity.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(settings.LockoutMinutes);
            identity.User.RequireUniqueEmail = true;
        });

        services.AddOptions<DataProtectionTokenProviderOptions>().Configure<IOptions<AuthOptions>>((token, auth) =>
            token.TokenLifespan = TimeSpan.FromMinutes(auth.Value.PasswordResetTokenMinutes));
    }
}
