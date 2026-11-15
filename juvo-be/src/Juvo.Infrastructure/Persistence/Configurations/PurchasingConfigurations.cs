using Juvo.Domain.Accounting;
using Juvo.Domain.Branches;
using Juvo.Domain.Catalog;
using Juvo.Domain.Purchasing;
using Juvo.Domain.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Juvo.Infrastructure.Persistence.Configurations;

internal sealed class SupplierConfiguration : IEntityTypeConfiguration<Supplier>
{
    public void Configure(EntityTypeBuilder<Supplier> builder)
    {
        builder.ToTable("Suppliers", t => t.HasCheckConstraint("CK_Suppliers_Terms", "[PaymentTermsDays] >= 0"));
        builder.Property(x => x.Name).HasMaxLength(160).IsRequired();
        builder.Property(x => x.ContactName).HasMaxLength(120);
        builder.Property(x => x.Phone).HasMaxLength(40);
        builder.Property(x => x.Email).HasMaxLength(200);
        builder.Property(x => x.Address).HasMaxLength(400);
        builder.Property(x => x.TaxId).HasMaxLength(60);
        builder.Property(x => x.Notes).HasMaxLength(1000);
        builder.HasIndex(x => x.Name);
    }
}

internal sealed class PurchaseOrderConfiguration : IEntityTypeConfiguration<PurchaseOrder>
{
    public void Configure(EntityTypeBuilder<PurchaseOrder> builder)
    {
        builder.ToTable("PurchaseOrders", t => t.HasCheckConstraint("CK_PurchaseOrders_Money", "[AmountPaid] >= 0 AND ([InvoiceAmount] IS NULL OR [InvoiceAmount] >= 0)"));
        builder.Property(x => x.Number).HasMaxLength(40).IsRequired();
        builder.Property(x => x.Status).HasConversion<int>();
        builder.Property(x => x.PaymentStatus).HasConversion<int>();
        builder.Property(x => x.Notes).HasMaxLength(1000);
        builder.Property(x => x.Currency).HasMaxLength(3).IsRequired();
        builder.Property(x => x.CancelReason).HasMaxLength(300);
        builder.Property(x => x.SupplierInvoiceNumber).HasMaxLength(80);
        builder.HasIndex(x => x.Number).IsUnique();
        builder.HasIndex(x => new { x.BranchId, x.Status, x.CreatedAt });
        builder.HasIndex(x => new { x.SupplierId, x.Status });
        builder.HasIndex(x => new { x.PaymentStatus, x.DueDate });
        builder.HasOne<Branch>().WithMany().HasForeignKey(x => x.BranchId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Supplier>().WithMany().HasForeignKey(x => x.SupplierId).OnDelete(DeleteBehavior.Restrict);
        builder.HasMany(x => x.Lines).WithOne().HasForeignKey(l => l.PurchaseOrderId).OnDelete(DeleteBehavior.Cascade);
        builder.HasMany(x => x.History).WithOne().HasForeignKey(h => h.PurchaseOrderId).OnDelete(DeleteBehavior.Cascade);
        builder.HasMany(x => x.Attachments).WithOne().HasForeignKey(a => a.PurchaseOrderId).OnDelete(DeleteBehavior.Cascade);
        builder.Navigation(x => x.Lines).UsePropertyAccessMode(PropertyAccessMode.Field);
        builder.Navigation(x => x.History).UsePropertyAccessMode(PropertyAccessMode.Field);
        builder.Navigation(x => x.Attachments).UsePropertyAccessMode(PropertyAccessMode.Field);
        builder.Ignore(x => x.Total);
        builder.Ignore(x => x.ReceivedValue);
        builder.Ignore(x => x.AmountPayable);
        builder.Ignore(x => x.Outstanding);
    }
}

internal sealed class PurchaseOrderLineConfiguration : IEntityTypeConfiguration<PurchaseOrderLine>
{
    public void Configure(EntityTypeBuilder<PurchaseOrderLine> builder)
    {
        builder.ToTable("PurchaseOrderLines", t => t.HasCheckConstraint("CK_PurchaseOrderLines_Quantity", "[OrderedQuantity] > 0 AND [ReceivedQuantity] >= 0 AND [ReceivedQuantity] <= [OrderedQuantity] AND [RejectedQuantity] >= 0 AND [UnitCost] >= 0"));
        builder.Property(x => x.Sku).HasMaxLength(60).IsRequired();
        builder.Property(x => x.ProductName).HasMaxLength(200).IsRequired();
        builder.Property(x => x.ColorName).HasMaxLength(60);
        builder.Property(x => x.Size).HasMaxLength(40);
        builder.HasIndex(x => x.PurchaseOrderId);
        builder.HasIndex(x => x.VariantId);
        builder.HasOne<ProductVariant>().WithMany().HasForeignKey(x => x.VariantId).OnDelete(DeleteBehavior.Restrict);
        builder.Ignore(x => x.OutstandingQuantity);
    }
}

internal sealed class PurchaseStatusHistoryConfiguration : IEntityTypeConfiguration<PurchaseStatusHistory>
{
    public void Configure(EntityTypeBuilder<PurchaseStatusHistory> builder)
    {
        builder.ToTable("PurchaseStatusHistory");
        builder.Property(x => x.FromStatus).HasConversion<int?>();
        builder.Property(x => x.ToStatus).HasConversion<int>();
        builder.Property(x => x.Reason).HasMaxLength(300);
        builder.Property(x => x.ActorName).HasMaxLength(120);
        builder.HasIndex(x => new { x.PurchaseOrderId, x.At });
    }
}

internal sealed class PurchaseAttachmentConfiguration : IEntityTypeConfiguration<PurchaseAttachment>
{
    public void Configure(EntityTypeBuilder<PurchaseAttachment> builder)
    {
        builder.ToTable("PurchaseAttachments");
        builder.Property(x => x.Path).HasMaxLength(300).IsRequired();
        builder.Property(x => x.FileName).HasMaxLength(200).IsRequired();
        builder.Property(x => x.ContentType).HasMaxLength(100).IsRequired();
        builder.HasIndex(x => x.PurchaseOrderId);
    }
}

internal sealed class GoodsReceiptConfiguration : IEntityTypeConfiguration<GoodsReceipt>
{
    public void Configure(EntityTypeBuilder<GoodsReceipt> builder)
    {
        builder.ToTable("GoodsReceipts");
        builder.Property(x => x.Number).HasMaxLength(40).IsRequired();
        builder.Property(x => x.PurchaseOrderNumber).HasMaxLength(40).IsRequired();
        builder.Property(x => x.ReceivedByName).HasMaxLength(120);
        builder.Property(x => x.Note).HasMaxLength(500);
        builder.HasIndex(x => x.Number).IsUnique();
        builder.HasIndex(x => new { x.PurchaseOrderId, x.ReceivedAt });
        builder.HasIndex(x => new { x.BranchId, x.ReceivedAt });
        builder.HasOne<Branch>().WithMany().HasForeignKey(x => x.BranchId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<PurchaseOrder>().WithMany().HasForeignKey(x => x.PurchaseOrderId).OnDelete(DeleteBehavior.Restrict);
        builder.HasMany(x => x.Lines).WithOne().HasForeignKey(l => l.ReceiptId).OnDelete(DeleteBehavior.Cascade);
        builder.Navigation(x => x.Lines).UsePropertyAccessMode(PropertyAccessMode.Field);
    }
}

internal sealed class GoodsReceiptLineConfiguration : IEntityTypeConfiguration<GoodsReceiptLine>
{
    public void Configure(EntityTypeBuilder<GoodsReceiptLine> builder)
    {
        builder.ToTable("GoodsReceiptLines", t => t.HasCheckConstraint("CK_GoodsReceiptLines_Quantity", "[AcceptedQuantity] >= 0 AND [RejectedQuantity] >= 0 AND [AcceptedQuantity] + [RejectedQuantity] > 0"));
        builder.Property(x => x.Sku).HasMaxLength(60).IsRequired();
        builder.Property(x => x.Note).HasMaxLength(300);
        builder.HasIndex(x => x.ReceiptId);
        builder.HasIndex(x => x.VariantId);
        builder.HasIndex(x => x.ProductId);
        builder.HasOne<ProductVariant>().WithMany().HasForeignKey(x => x.VariantId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class PurchasePaymentConfiguration : IEntityTypeConfiguration<PurchasePayment>
{
    public void Configure(EntityTypeBuilder<PurchasePayment> builder)
    {
        builder.ToTable("PurchasePayments", t => t.HasCheckConstraint("CK_PurchasePayments_Amount", "[Amount] > 0"));
        builder.Property(x => x.Method).HasConversion<int>();
        builder.Property(x => x.Reference).HasMaxLength(120);
        builder.Property(x => x.Note).HasMaxLength(300);
        builder.Property(x => x.ActorName).HasMaxLength(120);
        builder.HasIndex(x => new { x.PurchaseOrderId, x.PaidOn });
        builder.HasIndex(x => new { x.BranchId, x.PaidOn });
        builder.HasOne<PurchaseOrder>().WithMany().HasForeignKey(x => x.PurchaseOrderId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<FinanceAccount>().WithMany().HasForeignKey(x => x.AccountId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class GlobalSequenceConfiguration : IEntityTypeConfiguration<GlobalSequence>
{
    public void Configure(EntityTypeBuilder<GlobalSequence> builder)
    {
        builder.ToTable("GlobalSequences");
        builder.Property(x => x.Kind).HasMaxLength(4).IsRequired();
        builder.Property(x => x.Period).HasMaxLength(4).IsRequired();
        builder.HasIndex(x => new { x.Kind, x.Period }).IsUnique();
    }
}
