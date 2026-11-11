using Juvo.Domain.Branches;
using Juvo.Domain.Catalog;
using Juvo.Domain.Customers;
using Juvo.Domain.Orders;
using Juvo.Domain.Returns;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Juvo.Infrastructure.Persistence.Configurations;

internal sealed class ReturnRequestConfiguration : IEntityTypeConfiguration<ReturnRequest>
{
    public void Configure(EntityTypeBuilder<ReturnRequest> builder)
    {
        builder.ToTable("ReturnRequests", t =>
        {
            t.HasCheckConstraint("CK_ReturnRequests_Amounts", "[ItemsRefund] >= 0 AND [DeliveryChargeRefund] >= 0 AND [ReturnCharge] >= 0 AND [RefundTotal] >= 0");
        });

        builder.Property(x => x.Number).HasMaxLength(40).IsRequired();
        builder.Property(x => x.OrderNumber).HasMaxLength(40).IsRequired();
        builder.Property(x => x.CustomerMobile).HasMaxLength(20).IsRequired();
        builder.Property(x => x.CustomerName).HasMaxLength(120);
        builder.Property(x => x.Kind).HasConversion<int>();
        builder.Property(x => x.Status).HasConversion<int>();
        builder.Property(x => x.Fault).HasConversion<int>();
        builder.Property(x => x.ReasonCode).HasMaxLength(30).IsRequired();
        builder.Property(x => x.ReasonText).HasMaxLength(1000);
        builder.Property(x => x.IdempotencyKey).HasMaxLength(80);
        builder.Property(x => x.RejectReason).HasMaxLength(500);
        builder.Property(x => x.PickupCourier).HasMaxLength(100);
        builder.Property(x => x.PickupNote).HasMaxLength(500);
        builder.Property(x => x.ReplacementOrderNumber).HasMaxLength(40);

        builder.HasIndex(x => x.Number).IsUnique();
        builder.HasIndex(x => new { x.CustomerId, x.IdempotencyKey }).IsUnique().HasFilter("[IdempotencyKey] IS NOT NULL");
        builder.HasIndex(x => new { x.BranchId, x.Status, x.RequestedAt });
        builder.HasIndex(x => new { x.CustomerId, x.RequestedAt });
        builder.HasIndex(x => x.OrderId);
        builder.HasIndex(x => x.CustomerMobile);

        builder.HasOne<Branch>().WithMany().HasForeignKey(x => x.BranchId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Order>().WithMany().HasForeignKey(x => x.OrderId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Customer>().WithMany().HasForeignKey(x => x.CustomerId).OnDelete(DeleteBehavior.Restrict);
        builder.HasMany(x => x.Items).WithOne().HasForeignKey(i => i.ReturnRequestId).OnDelete(DeleteBehavior.Cascade);
        builder.HasMany(x => x.History).WithOne().HasForeignKey(h => h.ReturnRequestId).OnDelete(DeleteBehavior.Cascade);
        builder.HasMany(x => x.Notes).WithOne().HasForeignKey(n => n.ReturnRequestId).OnDelete(DeleteBehavior.Cascade);
        builder.HasMany(x => x.Photos).WithOne().HasForeignKey(p => p.ReturnRequestId).OnDelete(DeleteBehavior.Cascade);
        builder.HasMany(x => x.Refunds).WithOne().HasForeignKey(r => r.ReturnRequestId).OnDelete(DeleteBehavior.Restrict);
        builder.Navigation(x => x.Items).UsePropertyAccessMode(PropertyAccessMode.Field);
        builder.Navigation(x => x.History).UsePropertyAccessMode(PropertyAccessMode.Field);
        builder.Navigation(x => x.Notes).UsePropertyAccessMode(PropertyAccessMode.Field);
        builder.Navigation(x => x.Photos).UsePropertyAccessMode(PropertyAccessMode.Field);
        builder.Navigation(x => x.Refunds).UsePropertyAccessMode(PropertyAccessMode.Field);
        builder.Ignore(x => x.TotalQuantity);
        builder.Ignore(x => x.DomainEvents);
    }
}

internal sealed class ReturnItemConfiguration : IEntityTypeConfiguration<ReturnItem>
{
    public void Configure(EntityTypeBuilder<ReturnItem> builder)
    {
        builder.ToTable("ReturnItems", t => t.HasCheckConstraint("CK_ReturnItems_Quantity", "[Quantity] > 0 AND [LineRefund] >= 0 AND [RefundAmount] >= 0"));
        builder.Property(x => x.Name).HasMaxLength(200).IsRequired();
        builder.Property(x => x.Sku).HasMaxLength(60).IsRequired();
        builder.Property(x => x.ColorName).HasMaxLength(60);
        builder.Property(x => x.Size).HasMaxLength(40);
        builder.Property(x => x.ImagePath).HasMaxLength(300);
        builder.Property(x => x.ExchangeSku).HasMaxLength(60);
        builder.Property(x => x.Condition).HasConversion<int?>();
        builder.HasIndex(x => x.ReturnRequestId);
        builder.HasIndex(x => x.OrderItemId);
        builder.HasOne<OrderItem>().WithMany().HasForeignKey(x => x.OrderItemId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<ProductVariant>().WithMany().HasForeignKey(x => x.VariantId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class ReturnStatusHistoryConfiguration : IEntityTypeConfiguration<ReturnStatusHistory>
{
    public void Configure(EntityTypeBuilder<ReturnStatusHistory> builder)
    {
        builder.ToTable("ReturnStatusHistory");
        builder.Property(x => x.FromStatus).HasMaxLength(30);
        builder.Property(x => x.ToStatus).HasMaxLength(30).IsRequired();
        builder.Property(x => x.Reason).HasMaxLength(500);
        builder.Property(x => x.ActorName).HasMaxLength(120);
        builder.HasIndex(x => new { x.ReturnRequestId, x.At });
    }
}

internal sealed class ReturnNoteConfiguration : IEntityTypeConfiguration<ReturnNote>
{
    public void Configure(EntityTypeBuilder<ReturnNote> builder)
    {
        builder.ToTable("ReturnNotes");
        builder.Property(x => x.Text).HasMaxLength(1000).IsRequired();
        builder.Property(x => x.AuthorName).HasMaxLength(120);
        builder.HasIndex(x => new { x.ReturnRequestId, x.At });
    }
}

internal sealed class ReturnPhotoConfiguration : IEntityTypeConfiguration<ReturnPhoto>
{
    public void Configure(EntityTypeBuilder<ReturnPhoto> builder)
    {
        builder.ToTable("ReturnPhotos");
        builder.Property(x => x.Path).HasMaxLength(300).IsRequired();
        builder.HasIndex(x => x.ReturnRequestId);
    }
}

internal sealed class ReturnRefundConfiguration : IEntityTypeConfiguration<ReturnRefund>
{
    public void Configure(EntityTypeBuilder<ReturnRefund> builder)
    {
        builder.ToTable("ReturnRefunds", t => t.HasCheckConstraint("CK_ReturnRefunds_Amount", "[Amount] > 0"));
        builder.Property(x => x.Method).HasConversion<int>();
        builder.Property(x => x.Status).HasConversion<int>();
        builder.Property(x => x.Reference).HasMaxLength(120).IsRequired();
        builder.Property(x => x.ActorName).HasMaxLength(120);
        builder.HasIndex(x => x.ReturnRequestId).IsUnique(); // one refund per return: a second one can never be written (no double refund)
        builder.HasIndex(x => x.OrderId);
    }
}

internal sealed class ReturnSequenceConfiguration : IEntityTypeConfiguration<ReturnSequence>
{
    public void Configure(EntityTypeBuilder<ReturnSequence> builder)
    {
        builder.ToTable("ReturnSequences");
        builder.Property(x => x.Period).HasMaxLength(4).IsRequired();
        builder.HasIndex(x => new { x.BranchId, x.Period }).IsUnique();
        builder.HasOne<Branch>().WithMany().HasForeignKey(x => x.BranchId).OnDelete(DeleteBehavior.Restrict);
    }
}
