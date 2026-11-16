using Juvo.Domain.Catalog;
using Juvo.Domain.Customers;
using Juvo.Domain.Marketing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Juvo.Infrastructure.Persistence.Configurations;

internal sealed class CustomerSegmentConfiguration : IEntityTypeConfiguration<CustomerSegment>
{
    public void Configure(EntityTypeBuilder<CustomerSegment> builder)
    {
        builder.ToTable("CustomerSegments");
        builder.Property(x => x.Name).HasMaxLength(120).IsRequired();
        builder.Property(x => x.Description).HasMaxLength(400);
        builder.Property(x => x.DefinitionJson).HasColumnType("nvarchar(max)").IsRequired();
        builder.Property(x => x.BuiltInCode).HasMaxLength(40);
        builder.HasIndex(x => x.Name).IsUnique();
        builder.HasIndex(x => x.BuiltInCode).IsUnique().HasFilter("[BuiltInCode] IS NOT NULL");
    }
}

internal sealed class CampaignConfiguration : IEntityTypeConfiguration<Campaign>
{
    public void Configure(EntityTypeBuilder<Campaign> builder)
    {
        builder.ToTable("Campaigns");
        builder.Property(x => x.Name).HasMaxLength(120).IsRequired();
        builder.Property(x => x.Channel).HasConversion<int>();
        builder.Property(x => x.Status).HasConversion<int>();
        builder.Property(x => x.SubjectEn).HasMaxLength(200);
        builder.Property(x => x.SubjectBn).HasMaxLength(200);
        builder.Property(x => x.BodyEn).HasColumnType("nvarchar(max)").IsRequired();
        builder.Property(x => x.BodyBn).HasColumnType("nvarchar(max)");
        builder.Property(x => x.CreatedByName).HasMaxLength(120);
        builder.HasIndex(x => new { x.Status, x.ScheduledAt });
        builder.HasIndex(x => x.SegmentId);
        builder.HasOne<CustomerSegment>().WithMany().HasForeignKey(x => x.SegmentId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class CampaignRecipientConfiguration : IEntityTypeConfiguration<CampaignRecipient>
{
    public void Configure(EntityTypeBuilder<CampaignRecipient> builder)
    {
        builder.ToTable("CampaignRecipients");
        builder.Property(x => x.Status).HasConversion<int>();
        builder.Property(x => x.Contact).HasMaxLength(200);
        builder.Property(x => x.SkipReason).HasMaxLength(30);
        builder.HasIndex(x => new { x.CampaignId, x.CustomerId }).IsUnique();
        builder.HasIndex(x => new { x.CampaignId, x.Status });
        builder.HasIndex(x => x.NotificationLogId);
        builder.HasOne<Campaign>().WithMany().HasForeignKey(x => x.CampaignId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<Customer>().WithMany().HasForeignKey(x => x.CustomerId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class AutomationConfiguration : IEntityTypeConfiguration<Automation>
{
    public void Configure(EntityTypeBuilder<Automation> builder)
    {
        builder.ToTable("Automations");
        builder.Property(x => x.Kind).HasConversion<int>();
        builder.Property(x => x.Channel).HasConversion<int>();
        builder.Property(x => x.SubjectEn).HasMaxLength(200);
        builder.Property(x => x.SubjectBn).HasMaxLength(200);
        builder.Property(x => x.BodyEn).HasColumnType("nvarchar(max)").IsRequired();
        builder.Property(x => x.BodyBn).HasColumnType("nvarchar(max)").IsRequired();
        builder.HasIndex(x => x.Kind).IsUnique();
    }
}

internal sealed class BackInStockSubscriptionConfiguration : IEntityTypeConfiguration<BackInStockSubscription>
{
    public void Configure(EntityTypeBuilder<BackInStockSubscription> builder)
    {
        builder.ToTable("BackInStockSubscriptions");
        builder.Property(x => x.Mobile).HasMaxLength(20).IsRequired();
        builder.Property(x => x.Email).HasMaxLength(200);
        builder.Property(x => x.Outcome).HasMaxLength(40);
        builder.HasIndex(x => new { x.VariantId, x.Mobile }).IsUnique().HasFilter("[NotifiedAt] IS NULL");
        builder.HasIndex(x => new { x.NotifiedAt, x.CreatedAt });
        builder.HasIndex(x => x.Mobile);
        builder.HasOne<Product>().WithMany().HasForeignKey(x => x.ProductId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<ProductVariant>().WithMany().HasForeignKey(x => x.VariantId).OnDelete(DeleteBehavior.NoAction);
    }
}
