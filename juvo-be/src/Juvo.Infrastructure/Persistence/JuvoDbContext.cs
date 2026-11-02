using System.Linq.Expressions;
using Juvo.Domain.Common;
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

        foreach (var entityType in modelBuilder.Model.GetEntityTypes())
        {
            var clr = entityType.ClrType;

            if (typeof(Entity).IsAssignableFrom(clr))
            {
                modelBuilder.Entity(clr, b =>
                {
                    b.Property(nameof(Entity.Id)).ValueGeneratedNever();
                    b.Property(nameof(Entity.RowVersion)).IsRowVersion();
                });
            }

            if (typeof(ISoftDeletable).IsAssignableFrom(clr) && entityType.BaseType is null)
            {
                SetSoftDeleteFilter(modelBuilder, clr);
            }
        }
    }

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        // Money everywhere is exact decimal (SRS 12.2). Individual entities may override.
        configurationBuilder.Properties<decimal>().HavePrecision(18, 2);
    }

    private static void SetSoftDeleteFilter(ModelBuilder modelBuilder, Type clrType)
    {
        var parameter = Expression.Parameter(clrType, "e");
        var isDeleted = Expression.Property(Expression.Convert(parameter, typeof(ISoftDeletable)), nameof(ISoftDeletable.IsDeleted));
        var filter = Expression.Lambda(Expression.Not(isDeleted), parameter);
        modelBuilder.Entity(clrType).HasQueryFilter(filter);
    }
}
