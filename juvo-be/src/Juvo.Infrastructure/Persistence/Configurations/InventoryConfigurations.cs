using Juvo.Domain.Branches;
using Juvo.Domain.Catalog;
using Juvo.Domain.Inventory;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Juvo.Infrastructure.Persistence.Configurations;

internal sealed class StockLevelConfiguration : IEntityTypeConfiguration<StockLevel>
{
    public void Configure(EntityTypeBuilder<StockLevel> builder)
    {
        // The database itself refuses negative stock and more reserved than on hand (NFR-019).
        builder.ToTable("StockLevels", t =>
        {
            t.HasCheckConstraint("CK_StockLevels_OnHand", "[OnHand] >= 0");
            t.HasCheckConstraint("CK_StockLevels_Reserved", "[Reserved] >= 0 AND [Reserved] <= [OnHand]");
            t.HasCheckConstraint("CK_StockLevels_ReorderLevel", "[ReorderLevel] >= 0");
        });

        builder.Ignore(x => x.Available);
        builder.Ignore(x => x.IsLow);
        builder.HasIndex(x => new { x.BranchId, x.VariantId }).IsUnique();
        builder.HasIndex(x => x.VariantId);
        builder.HasOne<Branch>().WithMany().HasForeignKey(x => x.BranchId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<ProductVariant>().WithMany().HasForeignKey(x => x.VariantId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class StockMovementConfiguration : IEntityTypeConfiguration<StockMovement>
{
    public void Configure(EntityTypeBuilder<StockMovement> builder)
    {
        builder.ToTable("StockMovements", t => t.HasCheckConstraint("CK_StockMovements_OnHandAfter", "[OnHandAfter] >= 0"));
        builder.Property(x => x.Reason).HasConversion<int>();
        builder.Property(x => x.Note).HasMaxLength(300);
        builder.Property(x => x.ReferenceType).HasMaxLength(30);
        builder.HasIndex(x => new { x.BranchId, x.VariantId, x.CreatedAt });
        builder.HasIndex(x => new { x.BranchId, x.CreatedAt });
        builder.HasIndex(x => x.ReferenceId);
        builder.HasOne<Branch>().WithMany().HasForeignKey(x => x.BranchId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<ProductVariant>().WithMany().HasForeignKey(x => x.VariantId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class StockTransferConfiguration : IEntityTypeConfiguration<StockTransfer>
{
    public void Configure(EntityTypeBuilder<StockTransfer> builder)
    {
        builder.ToTable("StockTransfers", t => t.HasCheckConstraint("CK_StockTransfers_Branches", "[FromBranchId] <> [ToBranchId]"));
        builder.Property(x => x.Number).HasMaxLength(30).IsRequired();
        builder.Property(x => x.Status).HasConversion<int>();
        builder.Property(x => x.Note).HasMaxLength(300);
        builder.Property(x => x.CancelReason).HasMaxLength(300);
        builder.HasIndex(x => x.Number).IsUnique();
        builder.HasIndex(x => new { x.FromBranchId, x.Status });
        builder.HasIndex(x => new { x.ToBranchId, x.Status });
        builder.HasOne<Branch>().WithMany().HasForeignKey(x => x.FromBranchId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Branch>().WithMany().HasForeignKey(x => x.ToBranchId).OnDelete(DeleteBehavior.Restrict);
        builder.HasMany(x => x.Lines).WithOne().HasForeignKey(l => l.TransferId).OnDelete(DeleteBehavior.Cascade);
        builder.Navigation(x => x.Lines).UsePropertyAccessMode(PropertyAccessMode.Field);
    }
}

internal sealed class StockTransferLineConfiguration : IEntityTypeConfiguration<StockTransferLine>
{
    public void Configure(EntityTypeBuilder<StockTransferLine> builder)
    {
        builder.ToTable("StockTransferLines", t => t.HasCheckConstraint("CK_StockTransferLines_Quantity", "[Quantity] > 0"));
        builder.HasIndex(x => new { x.TransferId, x.VariantId }).IsUnique();
        builder.HasOne<ProductVariant>().WithMany().HasForeignKey(x => x.VariantId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class StockCountConfiguration : IEntityTypeConfiguration<StockCount>
{
    public void Configure(EntityTypeBuilder<StockCount> builder)
    {
        builder.ToTable("StockCounts");
        builder.Property(x => x.Number).HasMaxLength(30).IsRequired();
        builder.Property(x => x.Status).HasConversion<int>();
        builder.Property(x => x.Note).HasMaxLength(300);
        builder.HasIndex(x => x.Number).IsUnique();
        builder.HasIndex(x => new { x.BranchId, x.Status });
        builder.HasOne<Branch>().WithMany().HasForeignKey(x => x.BranchId).OnDelete(DeleteBehavior.Restrict);
        builder.HasMany(x => x.Lines).WithOne().HasForeignKey(l => l.CountId).OnDelete(DeleteBehavior.Cascade);
        builder.Navigation(x => x.Lines).UsePropertyAccessMode(PropertyAccessMode.Field);
    }
}

internal sealed class StockCountLineConfiguration : IEntityTypeConfiguration<StockCountLine>
{
    public void Configure(EntityTypeBuilder<StockCountLine> builder)
    {
        builder.ToTable("StockCountLines", t =>
        {
            t.HasCheckConstraint("CK_StockCountLines_Expected", "[ExpectedQuantity] >= 0");
            t.HasCheckConstraint("CK_StockCountLines_Counted", "[CountedQuantity] IS NULL OR [CountedQuantity] >= 0");
        });
        builder.Ignore(x => x.Difference);
        builder.HasIndex(x => new { x.CountId, x.VariantId }).IsUnique();
        builder.HasOne<ProductVariant>().WithMany().HasForeignKey(x => x.VariantId).OnDelete(DeleteBehavior.Restrict);
    }
}
