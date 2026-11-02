using Juvo.Application.Abstractions;
using Juvo.Infrastructure.Options;
using Juvo.Infrastructure.Outbox;
using Juvo.Infrastructure.Persistence;
using Juvo.Infrastructure.Persistence.Interceptors;
using Juvo.Infrastructure.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Juvo.Infrastructure;

public static class DependencyInjection
{
    public const string ConnectionStringName = "Default";

    /// <summary>Registers the database, interceptors, unit of work, outbox, file storage and validated options.</summary>
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<StorageOptions>().Bind(configuration.GetSection(StorageOptions.SectionName))
            .ValidateDataAnnotations().ValidateOnStart();
        services.AddOptions<OutboxOptions>().Bind(configuration.GetSection(OutboxOptions.SectionName))
            .ValidateDataAnnotations().ValidateOnStart();
        services.AddOptions<DatabaseOptions>().Bind(configuration.GetSection(DatabaseOptions.SectionName))
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

            // Order matters: domain events become outbox rows first, then everything is stamped and audited.
            builder.AddInterceptors(
                provider.GetRequiredService<DomainEventsOutboxInterceptor>(),
                provider.GetRequiredService<AuditSaveChangesInterceptor>());
        });

        services.AddScoped<IUnitOfWork, UnitOfWork>();
        services.AddScoped<IOutboxWriter, OutboxWriter>();
        services.AddSingleton<IFileStorage, LocalFileStorage>();
        services.AddHostedService<OutboxProcessor>();

        return services;
    }
}
