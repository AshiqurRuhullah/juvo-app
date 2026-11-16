using Juvo.Domain.Orders;
using Juvo.Domain.Promotions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Juvo.Infrastructure.Persistence.Configurations;

internal sealed class PromotionConfiguration : IEntityTypeConfiguration<Promotion>
{
    public void Configure(EntityTypeBuilder<Promotion> builder)
    {
        builder.ToTable("Promotions", t =>
        {
            t.HasCheckConstraint("CK_Promotions_Window", "[StartsAt] IS NULL OR [EndsAt] IS NULL OR [EndsAt] > [StartsAt]");
            t.HasCheckConstraint("CK_Promotions_Limits", "[UsedCount] >= 0 AND ([UsageLimit] IS NULL OR [UsageLimit] > 0) AND ([MinCartValue] IS NULL OR [MinCartValue] >= 0)");
        });

        builder.Property(x => x.NameEn).HasMaxLength(150).IsRequired();
        builder.Property(x => x.NameBn).HasMaxLength(150);
        builder.Property(x => x.Type).HasConversion<int>();
        builder.Property(x => x.StackMode).HasConversion<int>();
        builder.Property(x => x.Channels).HasConversion<int>();
        builder.Property(x => x.DiscountKind).HasConversion<int>();
        builder.Property(x => x.BannerSlot).HasConversion<int>();
        builder.Property(x => x.GetDiscountPercent).HasPrecision(5, 2);
        builder.Property(x => x.ZoneCodes).HasMaxLength(300);
        builder.Property(x => x.LabelEn).HasMaxLength(80);
        builder.Property(x => x.LabelBn).HasMaxLength(80);
        builder.Property(x => x.Slug).HasMaxLength(100);
        builder.Property(x => x.TitleEn).HasMaxLength(200);
        builder.Property(x => x.TitleBn).HasMaxLength(200);
        builder.Property(x => x.BodyEn).HasMaxLength(8000);
        builder.Property(x => x.BodyBn).HasMaxLength(8000);
        builder.Property(x => x.HeroImagePath).HasMaxLength(300);
        builder.Property(x => x.BannerImagePath).HasMaxLength(300);

        builder.HasIndex(x => x.Slug).IsUnique().HasFilter("[Slug] IS NOT NULL");
        builder.HasIndex(x => new { x.IsEnabled, x.IsArchived, x.StartsAt, x.EndsAt });
        builder.HasIndex(x => new { x.Type, x.IsArchived });

        builder.HasMany(x => x.Targets).WithOne().HasForeignKey(t => t.PromotionId).OnDelete(DeleteBehavior.Cascade);
        builder.Navigation(x => x.Targets).UsePropertyAccessMode(PropertyAccessMode.Field);
        builder.Ignore(x => x.IsPriceType);
        builder.Ignore(x => x.IsOrderType);
    }
}

internal sealed class PromotionTargetConfiguration : IEntityTypeConfiguration<PromotionTarget>
{
    public void Configure(EntityTypeBuilder<PromotionTarget> builder)
    {
        builder.ToTable("PromotionTargets", t => t.HasCheckConstraint("CK_PromotionTargets_Quantity", "[Quantity] >= 1"));
        builder.Property(x => x.Kind).HasConversion<int>();
        builder.Property(x => x.Role).HasConversion<int>();
        builder.HasIndex(x => new { x.PromotionId, x.Role });
        builder.HasIndex(x => new { x.Kind, x.TargetId });
    }
}

internal sealed class OrderItemAdjustmentConfiguration : IEntityTypeConfiguration<OrderItemAdjustment>
{
    public void Configure(EntityTypeBuilder<OrderItemAdjustment> builder)
    {
        builder.ToTable("OrderItemAdjustments");
        builder.Property(x => x.SourceType).HasMaxLength(20).IsRequired();
        builder.Property(x => x.LabelEn).HasMaxLength(150).IsRequired();
        builder.Property(x => x.LabelBn).HasMaxLength(150).IsRequired();
        builder.Property(x => x.Level).HasMaxLength(10).IsRequired();
        builder.HasIndex(x => x.OrderId);
        builder.HasIndex(x => x.OrderItemId);
        builder.HasIndex(x => new { x.SourceId, x.SourceType });
    }
}
