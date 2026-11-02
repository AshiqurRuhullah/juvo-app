using Juvo.Domain.Platform;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Juvo.Infrastructure.Persistence.Configurations;

internal sealed class AuditLogConfiguration : IEntityTypeConfiguration<AuditLog>
{
    public void Configure(EntityTypeBuilder<AuditLog> builder)
    {
        builder.ToTable("AuditLogs");
        builder.Property(x => x.Entity).HasMaxLength(200).IsRequired();
        builder.Property(x => x.EntityId).HasMaxLength(100).IsRequired();
        builder.Property(x => x.Action).HasConversion<string>().HasMaxLength(20);
        builder.Property(x => x.OldValue).HasColumnType("nvarchar(max)");
        builder.Property(x => x.NewValue).HasColumnType("nvarchar(max)");
        builder.Property(x => x.CorrelationId).HasMaxLength(100);
        builder.Property(x => x.IpAddress).HasMaxLength(64);
        builder.HasIndex(x => new { x.Entity, x.EntityId });
        builder.HasIndex(x => x.OccurredAt);
        builder.HasIndex(x => x.UserId);
        builder.HasIndex(x => x.BranchId);
    }
}

internal sealed class OutboxMessageConfiguration : IEntityTypeConfiguration<OutboxMessage>
{
    public void Configure(EntityTypeBuilder<OutboxMessage> builder)
    {
        builder.ToTable("OutboxMessages");
        builder.Property(x => x.Type).HasMaxLength(200).IsRequired();
        builder.Property(x => x.Payload).HasColumnType("nvarchar(max)").IsRequired();
        builder.Property(x => x.Status).HasConversion<int>();
        builder.Property(x => x.LastError).HasMaxLength(2000);
        builder.Property(x => x.CorrelationId).HasMaxLength(100);
        builder.HasIndex(x => new { x.Status, x.NextAttemptAt });
    }
}

internal sealed class JobLogConfiguration : IEntityTypeConfiguration<JobLog>
{
    public void Configure(EntityTypeBuilder<JobLog> builder)
    {
        builder.ToTable("JobLogs");
        builder.Property(x => x.JobName).HasMaxLength(200).IsRequired();
        builder.Property(x => x.Reference).HasMaxLength(100);
        builder.Property(x => x.Status).HasConversion<int>();
        builder.Property(x => x.Error).HasMaxLength(2000);
        builder.Property(x => x.CorrelationId).HasMaxLength(100);
        builder.HasIndex(x => new { x.JobName, x.StartedAt });
        builder.HasIndex(x => x.Status);
    }
}
