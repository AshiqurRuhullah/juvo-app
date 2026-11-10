using Juvo.Domain.Catalog;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Juvo.Infrastructure.Persistence.Configurations;

internal sealed class CategoryConfiguration : IEntityTypeConfiguration<Category>
{
    public void Configure(EntityTypeBuilder<Category> builder)
    {
        builder.ToTable("Categories");
        builder.Property(x => x.NameEn).HasMaxLength(150).IsRequired();
        builder.Property(x => x.NameBn).HasMaxLength(150);
        builder.Property(x => x.Slug).HasMaxLength(160).IsRequired();
        builder.Property(x => x.ImagePath).HasMaxLength(300);

        // Slugs are unique among live categories only, so a deleted category does not block its slug.
        builder.HasIndex(x => x.Slug).IsUnique().HasFilter("[IsDeleted] = 0");
        builder.HasIndex(x => new { x.ParentId, x.SortOrder });
        builder.HasOne<Category>().WithMany().HasForeignKey(x => x.ParentId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class BrandConfiguration : IEntityTypeConfiguration<Brand>
{
    public void Configure(EntityTypeBuilder<Brand> builder)
    {
        builder.ToTable("Brands");
        builder.Property(x => x.NameEn).HasMaxLength(150).IsRequired();
        builder.Property(x => x.NameBn).HasMaxLength(150);
        builder.Property(x => x.Slug).HasMaxLength(160).IsRequired();
        builder.HasIndex(x => x.Slug).IsUnique().HasFilter("[IsDeleted] = 0");
    }
}

internal sealed class SizeChartConfiguration : IEntityTypeConfiguration<SizeChart>
{
    public void Configure(EntityTypeBuilder<SizeChart> builder)
    {
        builder.ToTable("SizeCharts");
        builder.Property(x => x.NameEn).HasMaxLength(150).IsRequired();
        builder.Property(x => x.NameBn).HasMaxLength(150);
        builder.Property(x => x.TableJson).IsRequired();
        builder.HasOne<Category>().WithMany().HasForeignKey(x => x.CategoryId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class ProductConfiguration : IEntityTypeConfiguration<Product>
{
    public void Configure(EntityTypeBuilder<Product> builder)
    {
        builder.ToTable("Products", t =>
        {
            t.HasCheckConstraint("CK_Products_RegularPrice", "[RegularPrice] >= 0");
            t.HasCheckConstraint("CK_Products_SalePrice", "[SalePrice] IS NULL OR ([SalePrice] >= 0 AND [SalePrice] < [RegularPrice])");
        });

        builder.Property(x => x.NameEn).HasMaxLength(200).IsRequired();
        builder.Property(x => x.NameBn).HasMaxLength(200);
        builder.Property(x => x.Slug).HasMaxLength(220).IsRequired();
        builder.Property(x => x.ShortDescription).HasMaxLength(500);
        builder.Property(x => x.Description).HasMaxLength(8000);
        builder.Property(x => x.Material).HasMaxLength(200);
        builder.Property(x => x.Care).HasMaxLength(500);
        builder.Property(x => x.Tags).HasMaxLength(500);
        builder.Property(x => x.LengthCm).HasPrecision(8, 2);
        builder.Property(x => x.WidthCm).HasPrecision(8, 2);
        builder.Property(x => x.HeightCm).HasPrecision(8, 2);
        builder.Property(x => x.Status).HasConversion<int>();
        builder.Property(x => x.TargetGroup).HasConversion<int>();
        builder.Property(x => x.MetaTitle).HasMaxLength(120);
        builder.Property(x => x.MetaDescription).HasMaxLength(320);
        builder.Property(x => x.SocialImagePath).HasMaxLength(300);
        builder.Property(x => x.VideoUrl).HasMaxLength(500);

        builder.HasIndex(x => x.Slug).IsUnique().HasFilter("[IsDeleted] = 0");
        builder.HasIndex(x => new { x.Status, x.CategoryId });
        builder.HasIndex(x => x.BrandId);

        builder.HasOne<Category>().WithMany().HasForeignKey(x => x.CategoryId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Brand>().WithMany().HasForeignKey(x => x.BrandId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<SizeChart>().WithMany().HasForeignKey(x => x.SizeChartId).OnDelete(DeleteBehavior.Restrict);

        builder.HasMany(x => x.Variants).WithOne().HasForeignKey(v => v.ProductId).OnDelete(DeleteBehavior.Restrict);
        builder.HasMany(x => x.Images).WithOne().HasForeignKey(i => i.ProductId).OnDelete(DeleteBehavior.Restrict);
        builder.Navigation(x => x.Variants).UsePropertyAccessMode(PropertyAccessMode.Field);
        builder.Navigation(x => x.Images).UsePropertyAccessMode(PropertyAccessMode.Field);
    }
}

internal sealed class ProductVariantConfiguration : IEntityTypeConfiguration<ProductVariant>
{
    public void Configure(EntityTypeBuilder<ProductVariant> builder)
    {
        builder.ToTable("ProductVariants", t =>
            t.HasCheckConstraint("CK_ProductVariants_PriceOverride", "[PriceOverride] IS NULL OR [PriceOverride] >= 0"));

        builder.Property(x => x.Sku).HasMaxLength(60).IsRequired();
        builder.Property(x => x.ColorName).HasMaxLength(60);
        builder.Property(x => x.ColorHex).HasMaxLength(9);
        builder.Property(x => x.Size).HasMaxLength(30);
        builder.Property(x => x.Barcode).HasMaxLength(60);

        // BR-B3: a SKU is unique across the system. Deleted variants release their SKU.
        builder.HasIndex(x => x.Sku).IsUnique().HasFilter("[IsDeleted] = 0");
        builder.HasIndex(x => x.Barcode).HasFilter("[Barcode] IS NOT NULL AND [IsDeleted] = 0");
        builder.HasIndex(x => x.ProductId);
    }
}

internal sealed class ProductImageConfiguration : IEntityTypeConfiguration<ProductImage>
{
    public void Configure(EntityTypeBuilder<ProductImage> builder)
    {
        builder.ToTable("ProductImages");
        builder.Property(x => x.Path).HasMaxLength(300).IsRequired();
        builder.Property(x => x.ColorName).HasMaxLength(60);
        builder.Property(x => x.AltText).HasMaxLength(200);
        builder.HasIndex(x => new { x.ProductId, x.SortOrder });
    }
}
