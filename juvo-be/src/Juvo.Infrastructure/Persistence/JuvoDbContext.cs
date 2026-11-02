using Juvo.Domain.Platform;
using Microsoft.EntityFrameworkCore;

namespace Juvo.Infrastructure.Persistence;

public sealed class JuvoDbContext(DbContextOptions<JuvoDbContext> options) : DbContext(options)
{
    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();

    public DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();

    public DbSet<JobLog> JobLogs => Set<JobLog>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(JuvoDbContext).Assembly);
        ModelDefaults.Apply(modelBuilder);
    }

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder) =>
        ModelDefaults.ConfigureConventions(configurationBuilder);
}
