using Juvo.Domain.Integrations;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Juvo.Infrastructure.Persistence.Configurations;

internal sealed class ApiKeyConfiguration : IEntityTypeConfiguration<ApiKey>
{
    public void Configure(EntityTypeBuilder<ApiKey> builder)
    {
        builder.ToTable("ApiKeys");
        builder.Property(x => x.Name).HasMaxLength(80).IsRequired();
        builder.Property(x => x.KeyPrefix).HasMaxLength(16).IsRequired();
        builder.Property(x => x.KeyHash).HasMaxLength(32).IsRequired();
        builder.Property(x => x.Scopes).HasMaxLength(200).IsRequired();
        builder.Property(x => x.LastIpHash).HasMaxLength(16);
        builder.Ignore(x => x.ScopeList);
        builder.HasIndex(x => x.KeyPrefix).IsUnique();
    }
}

internal sealed class WebhookEndpointConfiguration : IEntityTypeConfiguration<WebhookEndpoint>
{
    public void Configure(EntityTypeBuilder<WebhookEndpoint> builder)
    {
        builder.ToTable("WebhookEndpoints");
        builder.Property(x => x.Name).HasMaxLength(80).IsRequired();
        builder.Property(x => x.Url).HasMaxLength(500).IsRequired();
        builder.Property(x => x.SecretProtected).HasMaxLength(500).IsRequired();
        builder.Property(x => x.Events).HasMaxLength(300).IsRequired();
        builder.Ignore(x => x.EventList);
    }
}

internal sealed class WebhookDeliveryConfiguration : IEntityTypeConfiguration<WebhookDelivery>
{
    public void Configure(EntityTypeBuilder<WebhookDelivery> builder)
    {
        builder.ToTable("WebhookDeliveries");
        builder.Property(x => x.EventType).HasMaxLength(40).IsRequired();
        builder.Property(x => x.Payload).HasColumnType("nvarchar(max)").IsRequired();
        builder.Property(x => x.PayloadHash).HasMaxLength(64).IsRequired();
        builder.Property(x => x.Status).HasConversion<int>();
        builder.Property(x => x.Error).HasMaxLength(300);
        builder.HasIndex(x => new { x.EventId, x.EndpointId }).IsUnique();
        builder.HasIndex(x => new { x.Status, x.NextAttemptAt });
        builder.HasIndex(x => new { x.EndpointId, x.CreatedAt });
        builder.HasOne<WebhookEndpoint>().WithMany().HasForeignKey(x => x.EndpointId).OnDelete(DeleteBehavior.Cascade);
    }
}
