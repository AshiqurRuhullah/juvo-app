using Juvo.Domain.Carts;
using Juvo.Domain.Catalog;
using Juvo.Domain.Customers;
using Juvo.Domain.Delivery;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Juvo.Infrastructure.Persistence.Configurations;

internal sealed class CountryConfiguration : IEntityTypeConfiguration<Country>
{
    public void Configure(EntityTypeBuilder<Country> builder)
    {
        builder.ToTable("Countries");
        builder.Property(x => x.Code).HasMaxLength(2).IsRequired();
        builder.Property(x => x.NameEn).HasMaxLength(100).IsRequired();
        builder.Property(x => x.NameBn).HasMaxLength(100);
        builder.Property(x => x.PhoneCode).HasMaxLength(8).IsRequired();
        builder.HasIndex(x => x.Code).IsUnique();
        builder.HasOne<DeliveryZone>().WithMany().HasForeignKey(x => x.ZoneId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class DivisionConfiguration : IEntityTypeConfiguration<Division>
{
    public void Configure(EntityTypeBuilder<Division> builder)
    {
        builder.ToTable("Divisions");
        builder.Property(x => x.NameEn).HasMaxLength(100).IsRequired();
        builder.Property(x => x.NameBn).HasMaxLength(100);
        builder.HasIndex(x => x.SourceId).IsUnique();
    }
}

internal sealed class DistrictConfiguration : IEntityTypeConfiguration<District>
{
    public void Configure(EntityTypeBuilder<District> builder)
    {
        builder.ToTable("Districts");
        builder.Property(x => x.NameEn).HasMaxLength(100).IsRequired();
        builder.Property(x => x.NameBn).HasMaxLength(100);
        builder.HasIndex(x => x.SourceId).IsUnique();
        builder.HasIndex(x => x.DivisionId);
        builder.HasOne<Division>().WithMany().HasForeignKey(x => x.DivisionId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class UpazilaConfiguration : IEntityTypeConfiguration<Upazila>
{
    public void Configure(EntityTypeBuilder<Upazila> builder)
    {
        builder.ToTable("Upazilas");
        builder.Property(x => x.NameEn).HasMaxLength(100).IsRequired();
        builder.Property(x => x.NameBn).HasMaxLength(100);
        builder.HasIndex(x => x.SourceId).IsUnique().HasFilter("[SourceId] IS NOT NULL");
        builder.HasIndex(x => new { x.DistrictId, x.NameEn }).IsUnique();
        builder.HasOne<District>().WithMany().HasForeignKey(x => x.DistrictId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class DeliveryZoneConfiguration : IEntityTypeConfiguration<DeliveryZone>
{
    public void Configure(EntityTypeBuilder<DeliveryZone> builder)
    {
        builder.ToTable("DeliveryZones", t =>
        {
            t.HasCheckConstraint("CK_DeliveryZones_Fees", "[BaseFee] >= 0 AND [RemoteSurcharge] >= 0 AND [PerExtraKgFee] >= 0 AND ([FreeDeliveryThreshold] IS NULL OR [FreeDeliveryThreshold] >= 0)");
            t.HasCheckConstraint("CK_DeliveryZones_Days", "[DaysMin] >= 0 AND [DaysMax] >= [DaysMin]");
        });
        builder.Property(x => x.Code).HasMaxLength(30).IsRequired();
        builder.Property(x => x.NameEn).HasMaxLength(100).IsRequired();
        builder.Property(x => x.NameBn).HasMaxLength(100);
        builder.Property(x => x.Kind).HasConversion<int>();
        builder.HasIndex(x => x.Code).IsUnique();
        builder.HasMany(x => x.Slabs).WithOne().HasForeignKey(s => s.ZoneId).OnDelete(DeleteBehavior.Cascade);
        builder.HasMany(x => x.Areas).WithOne().HasForeignKey(a => a.ZoneId).OnDelete(DeleteBehavior.Cascade);
        builder.Navigation(x => x.Slabs).UsePropertyAccessMode(PropertyAccessMode.Field);
        builder.Navigation(x => x.Areas).UsePropertyAccessMode(PropertyAccessMode.Field);
    }
}

internal sealed class DeliveryWeightSlabConfiguration : IEntityTypeConfiguration<DeliveryWeightSlab>
{
    public void Configure(EntityTypeBuilder<DeliveryWeightSlab> builder)
    {
        builder.ToTable("DeliveryWeightSlabs", t => t.HasCheckConstraint("CK_DeliveryWeightSlabs_Values", "[UpToGrams] > 0 AND [ExtraFee] >= 0"));
        builder.HasIndex(x => new { x.ZoneId, x.UpToGrams }).IsUnique();
    }
}

internal sealed class DeliveryZoneAreaConfiguration : IEntityTypeConfiguration<DeliveryZoneArea>
{
    public void Configure(EntityTypeBuilder<DeliveryZoneArea> builder)
    {
        builder.ToTable("DeliveryZoneAreas");
        builder.HasIndex(x => new { x.DistrictId, x.UpazilaId });
        builder.HasOne<District>().WithMany().HasForeignKey(x => x.DistrictId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Upazila>().WithMany().HasForeignKey(x => x.UpazilaId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class AddressConfiguration : IEntityTypeConfiguration<Address>
{
    public void Configure(EntityTypeBuilder<Address> builder)
    {
        builder.ToTable("Addresses");
        builder.Property(x => x.Label).HasMaxLength(50);
        builder.Property(x => x.RecipientName).HasMaxLength(120).IsRequired();
        builder.Property(x => x.Phone).HasMaxLength(25).IsRequired();
        builder.Property(x => x.CountryCode).HasMaxLength(2).IsRequired();
        builder.Property(x => x.Area).HasMaxLength(120);
        builder.Property(x => x.AddressLine).HasMaxLength(300).IsRequired();
        builder.Property(x => x.Landmark).HasMaxLength(200);
        builder.Property(x => x.StateProvince).HasMaxLength(100);
        builder.Property(x => x.City).HasMaxLength(100);
        builder.Property(x => x.PostalCode).HasMaxLength(20);
        builder.HasIndex(x => x.CustomerId);
        builder.HasOne<Customer>().WithMany().HasForeignKey(x => x.CustomerId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Division>().WithMany().HasForeignKey(x => x.DivisionId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<District>().WithMany().HasForeignKey(x => x.DistrictId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Upazila>().WithMany().HasForeignKey(x => x.UpazilaId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class WishlistItemConfiguration : IEntityTypeConfiguration<WishlistItem>
{
    public void Configure(EntityTypeBuilder<WishlistItem> builder)
    {
        builder.ToTable("WishlistItems");
        builder.HasIndex(x => new { x.CustomerId, x.ProductId }).IsUnique();
        builder.HasOne<Customer>().WithMany().HasForeignKey(x => x.CustomerId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<Product>().WithMany().HasForeignKey(x => x.ProductId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class ConsentLogConfiguration : IEntityTypeConfiguration<ConsentLog>
{
    public void Configure(EntityTypeBuilder<ConsentLog> builder)
    {
        builder.ToTable("ConsentLogs");
        builder.Property(x => x.Type).HasConversion<int>();
        builder.Property(x => x.Source).HasMaxLength(50).IsRequired();
        builder.Property(x => x.IpAddress).HasMaxLength(64);
        builder.HasIndex(x => new { x.CustomerId, x.Type, x.At });
        builder.HasOne<Customer>().WithMany().HasForeignKey(x => x.CustomerId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class CouponConfiguration : IEntityTypeConfiguration<Coupon>
{
    public void Configure(EntityTypeBuilder<Coupon> builder)
    {
        builder.ToTable("Coupons", t =>
        {
            t.HasCheckConstraint("CK_Coupons_Value", "[Value] > 0 AND ([Kind] <> 1 OR [Value] <= 100)");
            t.HasCheckConstraint("CK_Coupons_Limits", "[MinOrderValue] >= 0 AND ([MaxDiscount] IS NULL OR [MaxDiscount] > 0) AND ([UsageLimit] IS NULL OR [UsageLimit] > 0) AND [UsedCount] >= 0");
        });
        builder.Property(x => x.Code).HasMaxLength(40).IsRequired();
        builder.Property(x => x.Description).HasMaxLength(200);
        builder.Property(x => x.Kind).HasConversion<int>();
        builder.HasIndex(x => x.Code).IsUnique();
    }
}

internal sealed class CartConfiguration : IEntityTypeConfiguration<Cart>
{
    public void Configure(EntityTypeBuilder<Cart> builder)
    {
        builder.ToTable("Carts");
        builder.Property(x => x.TokenHash).HasMaxLength(64);
        // A guest finds the cart by its hashed token; a customer has at most one cart.
        builder.HasIndex(x => x.TokenHash).IsUnique().HasFilter("[TokenHash] IS NOT NULL");
        builder.HasIndex(x => x.CustomerId).IsUnique().HasFilter("[CustomerId] IS NOT NULL");
        builder.HasIndex(x => x.ExpiresAt);
        builder.HasOne<Customer>().WithMany().HasForeignKey(x => x.CustomerId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<Coupon>().WithMany().HasForeignKey(x => x.CouponId).OnDelete(DeleteBehavior.Restrict);
        builder.HasMany(x => x.Items).WithOne().HasForeignKey(i => i.CartId).OnDelete(DeleteBehavior.Cascade);
        builder.Navigation(x => x.Items).UsePropertyAccessMode(PropertyAccessMode.Field);
    }
}

internal sealed class CartItemConfiguration : IEntityTypeConfiguration<CartItem>
{
    public void Configure(EntityTypeBuilder<CartItem> builder)
    {
        builder.ToTable("CartItems", t => t.HasCheckConstraint("CK_CartItems_Quantity", "[Quantity] > 0"));
        builder.HasIndex(x => new { x.CartId, x.VariantId }).IsUnique();
        builder.HasOne<ProductVariant>().WithMany().HasForeignKey(x => x.VariantId).OnDelete(DeleteBehavior.Restrict);
    }
}
