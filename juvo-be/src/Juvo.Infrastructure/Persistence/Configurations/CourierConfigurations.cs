using Juvo.Domain.Branches;
using Juvo.Domain.Couriers;
using Juvo.Domain.Orders;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Juvo.Infrastructure.Persistence.Configurations;

internal sealed class CourierConfiguration : IEntityTypeConfiguration<Courier>
{
    public void Configure(EntityTypeBuilder<Courier> builder)
    {
        builder.ToTable("Couriers", t => t.HasCheckConstraint("CK_Couriers_Rates", "[BaseRate] >= 0 AND [PerExtraKgRate] >= 0 AND [BaseRateUpToGrams] > 0 AND ([ChargeOverride] IS NULL OR [ChargeOverride] >= 0)"));
        builder.Property(x => x.Code).HasMaxLength(30).IsRequired();
        builder.Property(x => x.Name).HasMaxLength(120).IsRequired();
        builder.Property(x => x.Provider).HasMaxLength(30).IsRequired();
        builder.Property(x => x.SupportedZones).HasMaxLength(400).IsRequired();
        builder.Property(x => x.TrackingUrlTemplate).HasMaxLength(300);
        builder.Property(x => x.Notes).HasMaxLength(500);
        builder.Property(x => x.SettingsJson).HasMaxLength(2000).IsRequired();
        builder.HasIndex(x => x.Code).IsUnique().HasFilter("[IsDeleted] = 0");
        builder.HasIndex(x => new { x.IsEnabled, x.Priority });
        builder.Ignore(x => x.ZoneCodes);
        builder.Ignore(x => x.Settings);
    }
}

internal sealed class ShipmentConfiguration : IEntityTypeConfiguration<Shipment>
{
    public void Configure(EntityTypeBuilder<Shipment> builder)
    {
        builder.ToTable("Shipments", t => t.HasCheckConstraint("CK_Shipments_Amounts", "[CodAmount] >= 0 AND [Cost] >= 0 AND [WeightGrams] > 0 AND [Attempts] >= 0"));
        builder.Property(x => x.OrderNumber).HasMaxLength(40).IsRequired();
        builder.Property(x => x.CourierCode).HasMaxLength(30).IsRequired();
        builder.Property(x => x.CourierName).HasMaxLength(120).IsRequired();
        builder.Property(x => x.Kind).HasConversion<int>();
        builder.Property(x => x.Status).HasConversion<int>();
        builder.Property(x => x.ConsignmentId).HasMaxLength(80).IsRequired();
        builder.Property(x => x.ProviderReference).HasMaxLength(120);
        builder.Property(x => x.TrackingUrl).HasMaxLength(500);
        builder.Property(x => x.PickupName).HasMaxLength(120).IsRequired();
        builder.Property(x => x.PickupPhone).HasMaxLength(30);
        builder.Property(x => x.PickupAddress).HasMaxLength(400).IsRequired();
        builder.Property(x => x.DeliveryName).HasMaxLength(120).IsRequired();
        builder.Property(x => x.DeliveryPhone).HasMaxLength(30).IsRequired();
        builder.Property(x => x.DeliveryAddress).HasMaxLength(600).IsRequired();
        builder.Property(x => x.DeliveryDistrict).HasMaxLength(100);
        builder.Property(x => x.Note).HasMaxLength(500);
        builder.Property(x => x.FailureReason).HasMaxLength(500);
        builder.Property(x => x.LastRawStatus).HasMaxLength(60);
        builder.Property(x => x.CancelReason).HasMaxLength(300);

        builder.HasIndex(x => new { x.CourierId, x.ConsignmentId }).IsUnique();
        builder.HasIndex(x => new { x.BranchId, x.Status, x.BookedAt });
        builder.HasIndex(x => new { x.OrderId, x.BookedAt });
        builder.HasIndex(x => x.CodSettlementId);
        builder.HasIndex(x => x.OrderNumber);

        // One open forward parcel per order and one open reverse parcel per return: a second simultaneous booking fails on this index.
        // Open = Booked 1, PickedUp 2, InTransit 3, OutForDelivery 4, DeliveryFailed 6.
        builder.HasIndex(x => x.OrderId).IsUnique().HasFilter("[Kind] = 1 AND [Status] IN (1, 2, 3, 4, 6) AND [IsDeleted] = 0").HasDatabaseName("UX_Shipments_OpenForward");
        builder.HasIndex(x => x.ReturnRequestId).IsUnique().HasFilter("[Kind] = 2 AND [Status] IN (1, 2, 3, 4, 6) AND [IsDeleted] = 0").HasDatabaseName("UX_Shipments_OpenReturn");

        builder.HasOne<Branch>().WithMany().HasForeignKey(x => x.BranchId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Order>().WithMany().HasForeignKey(x => x.OrderId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Courier>().WithMany().HasForeignKey(x => x.CourierId).OnDelete(DeleteBehavior.Restrict);
        builder.HasMany(x => x.Events).WithOne().HasForeignKey(e => e.ShipmentId).OnDelete(DeleteBehavior.Cascade);
        builder.Navigation(x => x.Events).UsePropertyAccessMode(PropertyAccessMode.Field);
        builder.Ignore(x => x.IsOpen);
        builder.Ignore(x => x.DomainEvents);
    }
}

internal sealed class ShipmentEventConfiguration : IEntityTypeConfiguration<ShipmentEvent>
{
    public void Configure(EntityTypeBuilder<ShipmentEvent> builder)
    {
        builder.ToTable("ShipmentEvents");
        builder.Property(x => x.ProviderEventId).HasMaxLength(150);
        builder.Property(x => x.RawStatus).HasMaxLength(60).IsRequired();
        builder.Property(x => x.Status).HasConversion<int>();
        builder.Property(x => x.Description).HasMaxLength(500);
        builder.Property(x => x.Location).HasMaxLength(200);
        builder.Property(x => x.Source).HasConversion<int>();
        builder.Property(x => x.ActorName).HasMaxLength(120);
        builder.HasIndex(x => new { x.ShipmentId, x.OccurredAt });
        builder.HasIndex(x => new { x.ShipmentId, x.ProviderEventId }).IsUnique().HasFilter("[ProviderEventId] IS NOT NULL");
    }
}

internal sealed class CodSettlementConfiguration : IEntityTypeConfiguration<CodSettlement>
{
    public void Configure(EntityTypeBuilder<CodSettlement> builder)
    {
        builder.ToTable("CodSettlements");
        builder.Property(x => x.Number).HasMaxLength(40).IsRequired();
        builder.Property(x => x.CourierName).HasMaxLength(120).IsRequired();
        builder.Property(x => x.Status).HasConversion<int>();
        builder.Property(x => x.Reference).HasMaxLength(120);
        builder.Property(x => x.Note).HasMaxLength(500);
        builder.Property(x => x.CreatedByName).HasMaxLength(120);
        builder.Property(x => x.ReconciledByName).HasMaxLength(120);
        builder.Property(x => x.SettledByName).HasMaxLength(120);
        builder.HasIndex(x => x.Number).IsUnique();
        builder.HasIndex(x => new { x.BranchId, x.Status, x.CreatedAt });
        builder.HasIndex(x => new { x.CourierId, x.Status });
        builder.HasOne<Branch>().WithMany().HasForeignKey(x => x.BranchId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Courier>().WithMany().HasForeignKey(x => x.CourierId).OnDelete(DeleteBehavior.Restrict);
        builder.HasMany(x => x.Items).WithOne().HasForeignKey(i => i.SettlementId).OnDelete(DeleteBehavior.Cascade);
        builder.Navigation(x => x.Items).UsePropertyAccessMode(PropertyAccessMode.Field);
        builder.Ignore(x => x.Difference);
    }
}

internal sealed class CodSettlementItemConfiguration : IEntityTypeConfiguration<CodSettlementItem>
{
    public void Configure(EntityTypeBuilder<CodSettlementItem> builder)
    {
        builder.ToTable("CodSettlementItems");
        builder.Property(x => x.OrderNumber).HasMaxLength(40).IsRequired();
        builder.Property(x => x.ConsignmentId).HasMaxLength(80).IsRequired();
        builder.Property(x => x.Note).HasMaxLength(300);
        builder.HasIndex(x => x.ShipmentId);
        builder.HasIndex(x => x.SettlementId);
        builder.Ignore(x => x.Difference);
    }
}

internal sealed class SandboxConsignmentConfiguration : IEntityTypeConfiguration<SandboxConsignment>
{
    public void Configure(EntityTypeBuilder<SandboxConsignment> builder)
    {
        builder.ToTable("SandboxConsignments");
        builder.Property(x => x.ConsignmentId).HasMaxLength(80).IsRequired();
        builder.Property(x => x.Reference).HasMaxLength(60).IsRequired();
        builder.Property(x => x.RawStatus).HasMaxLength(60).IsRequired();
        builder.Property(x => x.Note).HasMaxLength(500);
        builder.HasIndex(x => x.ConsignmentId).IsUnique();
    }
}
