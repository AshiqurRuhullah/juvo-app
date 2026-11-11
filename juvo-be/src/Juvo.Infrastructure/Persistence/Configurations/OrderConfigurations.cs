using Juvo.Domain.Branches;
using Juvo.Domain.Catalog;
using Juvo.Domain.Customers;
using Juvo.Domain.Orders;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Juvo.Infrastructure.Persistence.Configurations;

internal sealed class OrderConfiguration : IEntityTypeConfiguration<Order>
{
    public void Configure(EntityTypeBuilder<Order> builder)
    {
        builder.ToTable("Orders", t =>
        {
            t.HasCheckConstraint("CK_Orders_Amounts", "[Subtotal] >= 0 AND [DeliveryCharge] >= 0 AND [GrandTotal] >= 0 AND [AdvanceRequired] >= 0");
        });

        builder.Property(x => x.Number).HasMaxLength(40).IsRequired();
        builder.Property(x => x.Source).HasConversion<int>();
        builder.Property(x => x.Status).HasConversion<int>();
        builder.Property(x => x.PaymentStatus).HasConversion<int>();
        builder.Property(x => x.DeliveryStatus).HasConversion<int>();
        builder.Property(x => x.FraudState).HasConversion<int>();
        builder.Property(x => x.PaymentMethod).HasMaxLength(30).IsRequired();
        builder.Property(x => x.CustomerMobile).HasMaxLength(20).IsRequired();
        builder.Property(x => x.CustomerName).HasMaxLength(120);
        builder.Property(x => x.CustomerEmail).HasMaxLength(254);
        builder.Property(x => x.ShipName).HasMaxLength(120).IsRequired();
        builder.Property(x => x.ShipPhone).HasMaxLength(20).IsRequired();
        builder.Property(x => x.ShipCountryCode).HasMaxLength(2).IsRequired();
        builder.Property(x => x.ShipDivision).HasMaxLength(100);
        builder.Property(x => x.ShipDistrict).HasMaxLength(100);
        builder.Property(x => x.ShipUpazila).HasMaxLength(100);
        builder.Property(x => x.ShipArea).HasMaxLength(150);
        builder.Property(x => x.ShipAddressLine).HasMaxLength(300).IsRequired();
        builder.Property(x => x.ShipLandmark).HasMaxLength(150);
        builder.Property(x => x.ShipStateProvince).HasMaxLength(100);
        builder.Property(x => x.ShipCity).HasMaxLength(100);
        builder.Property(x => x.ShipPostalCode).HasMaxLength(20);
        builder.Property(x => x.Currency).HasMaxLength(3).IsRequired();
        builder.Property(x => x.CouponCode).HasMaxLength(40);
        builder.Property(x => x.ZoneCode).HasMaxLength(40);
        builder.Property(x => x.TaxRuleCode).HasMaxLength(30);
        builder.Property(x => x.TaxRatePercent).HasPrecision(5, 2);
        builder.Property(x => x.RoutingRule).HasMaxLength(20).IsRequired();
        builder.Property(x => x.CancelReason).HasMaxLength(300);
        builder.Property(x => x.IdempotencyKey).HasMaxLength(80);
        builder.Property(x => x.Fingerprint).HasMaxLength(64).IsRequired();
        builder.Property(x => x.TrackingTokenHash).HasMaxLength(64).IsRequired();
        builder.Property(x => x.DeviceId).HasMaxLength(80);
        builder.Property(x => x.ClientIp).HasMaxLength(45);

        builder.HasIndex(x => x.Number).IsUnique();
        builder.HasIndex(x => x.IdempotencyKey).IsUnique().HasFilter("[IdempotencyKey] IS NOT NULL");
        builder.HasIndex(x => x.TrackingTokenHash);
        builder.HasIndex(x => new { x.BranchId, x.Status, x.PlacedAt });
        builder.HasIndex(x => new { x.CustomerId, x.PlacedAt });
        builder.HasIndex(x => new { x.CustomerMobile, x.PlacedAt });
        builder.HasIndex(x => new { x.DeviceId, x.PlacedAt });
        builder.HasIndex(x => new { x.ClientIp, x.PlacedAt });
        builder.HasIndex(x => new { x.PaymentStatus, x.PaymentHoldExpiresAt });
        builder.HasIndex(x => x.FraudState);

        builder.HasOne<Branch>().WithMany().HasForeignKey(x => x.BranchId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Customer>().WithMany().HasForeignKey(x => x.CustomerId).OnDelete(DeleteBehavior.Restrict);
        builder.HasMany(x => x.Items).WithOne().HasForeignKey(i => i.OrderId).OnDelete(DeleteBehavior.Cascade);
        builder.HasMany(x => x.History).WithOne().HasForeignKey(h => h.OrderId).OnDelete(DeleteBehavior.Cascade);
        builder.HasMany(x => x.Notes).WithOne().HasForeignKey(n => n.OrderId).OnDelete(DeleteBehavior.Cascade);
        builder.HasMany(x => x.Flags).WithOne().HasForeignKey(f => f.OrderId).OnDelete(DeleteBehavior.Cascade);
        builder.Navigation(x => x.Items).UsePropertyAccessMode(PropertyAccessMode.Field);
        builder.Navigation(x => x.History).UsePropertyAccessMode(PropertyAccessMode.Field);
        builder.Navigation(x => x.Notes).UsePropertyAccessMode(PropertyAccessMode.Field);
        builder.Navigation(x => x.Flags).UsePropertyAccessMode(PropertyAccessMode.Field);
        builder.Ignore(x => x.IsHeld);
        builder.Ignore(x => x.IsOnlinePayment);
        builder.Ignore(x => x.DomainEvents);
    }
}

internal sealed class OrderItemConfiguration : IEntityTypeConfiguration<OrderItem>
{
    public void Configure(EntityTypeBuilder<OrderItem> builder)
    {
        builder.ToTable("OrderItems", t => t.HasCheckConstraint("CK_OrderItems_Quantity", "[Quantity] > 0 AND [UnitPrice] >= 0"));
        builder.Property(x => x.Name).HasMaxLength(200).IsRequired();
        builder.Property(x => x.NameBn).HasMaxLength(200);
        builder.Property(x => x.Sku).HasMaxLength(60).IsRequired();
        builder.Property(x => x.ColorName).HasMaxLength(60);
        builder.Property(x => x.Size).HasMaxLength(40);
        builder.Property(x => x.ImagePath).HasMaxLength(300);
        builder.HasIndex(x => x.OrderId);
        builder.HasIndex(x => x.VariantId);
        builder.HasOne<ProductVariant>().WithMany().HasForeignKey(x => x.VariantId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class OrderStatusHistoryConfiguration : IEntityTypeConfiguration<OrderStatusHistory>
{
    public void Configure(EntityTypeBuilder<OrderStatusHistory> builder)
    {
        builder.ToTable("OrderStatusHistory");
        builder.Property(x => x.Track).HasConversion<int>();
        builder.Property(x => x.FromStatus).HasMaxLength(30);
        builder.Property(x => x.ToStatus).HasMaxLength(30).IsRequired();
        builder.Property(x => x.Reason).HasMaxLength(300);
        builder.Property(x => x.ActorName).HasMaxLength(120);
        builder.HasIndex(x => new { x.OrderId, x.At });
    }
}

internal sealed class OrderNoteConfiguration : IEntityTypeConfiguration<OrderNote>
{
    public void Configure(EntityTypeBuilder<OrderNote> builder)
    {
        builder.ToTable("OrderNotes");
        builder.Property(x => x.Kind).HasConversion<int>();
        builder.Property(x => x.Text).HasMaxLength(1000).IsRequired();
        builder.Property(x => x.AuthorName).HasMaxLength(120);
        builder.HasIndex(x => new { x.OrderId, x.At });
    }
}

internal sealed class OrderRiskFlagConfiguration : IEntityTypeConfiguration<OrderRiskFlag>
{
    public void Configure(EntityTypeBuilder<OrderRiskFlag> builder)
    {
        builder.ToTable("OrderRiskFlags");
        builder.Property(x => x.RuleCode).HasMaxLength(40).IsRequired();
        builder.Property(x => x.Message).HasMaxLength(300).IsRequired();
        builder.Property(x => x.Action).HasConversion<int>();
        builder.HasIndex(x => x.OrderId);
    }
}

internal sealed class FraudReviewLogConfiguration : IEntityTypeConfiguration<FraudReviewLog>
{
    public void Configure(EntityTypeBuilder<FraudReviewLog> builder)
    {
        builder.ToTable("FraudReviewLogs");
        builder.Property(x => x.Action).HasConversion<int>();
        builder.Property(x => x.FromState).HasConversion<int>();
        builder.Property(x => x.ToState).HasConversion<int>();
        builder.Property(x => x.Note).HasMaxLength(500);
        builder.Property(x => x.ActorName).HasMaxLength(120);
        builder.HasIndex(x => new { x.OrderId, x.At });
        builder.HasOne<Order>().WithMany().HasForeignKey(x => x.OrderId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class OrderSequenceConfiguration : IEntityTypeConfiguration<OrderSequence>
{
    public void Configure(EntityTypeBuilder<OrderSequence> builder)
    {
        builder.ToTable("OrderSequences");
        builder.Property(x => x.Period).HasMaxLength(4).IsRequired();
        builder.HasIndex(x => new { x.BranchId, x.Period }).IsUnique();
        builder.HasOne<Branch>().WithMany().HasForeignKey(x => x.BranchId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class BlacklistEntryConfiguration : IEntityTypeConfiguration<BlacklistEntry>
{
    public void Configure(EntityTypeBuilder<BlacklistEntry> builder)
    {
        builder.ToTable("BlacklistEntries");
        builder.Property(x => x.Kind).HasConversion<int>();
        builder.Property(x => x.Value).HasMaxLength(300).IsRequired();
        builder.Property(x => x.Note).HasMaxLength(300);
        builder.HasIndex(x => new { x.Kind, x.Value });
    }
}

internal sealed class PlatformSettingConfiguration : IEntityTypeConfiguration<PlatformSetting>
{
    public void Configure(EntityTypeBuilder<PlatformSetting> builder)
    {
        builder.ToTable("PlatformSettings");
        builder.Property(x => x.Key).HasMaxLength(100).IsRequired();
        builder.Property(x => x.Value).HasMaxLength(2000).IsRequired();
        builder.HasIndex(x => x.Key).IsUnique();
    }
}
