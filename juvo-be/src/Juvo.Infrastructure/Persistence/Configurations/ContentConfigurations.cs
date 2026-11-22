using Juvo.Domain.Content;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Juvo.Infrastructure.Persistence.Configurations;

internal sealed class ContentPageConfiguration : IEntityTypeConfiguration<ContentPage>
{
    public void Configure(EntityTypeBuilder<ContentPage> builder)
    {
        builder.ToTable("ContentPages");
        builder.Property(x => x.Key).HasMaxLength(40).IsRequired();
        builder.Property(x => x.TitleEn).HasMaxLength(200);
        builder.Property(x => x.TitleBn).HasMaxLength(200);
        builder.Property(x => x.BodyEn).HasColumnType("nvarchar(max)");
        builder.Property(x => x.BodyBn).HasColumnType("nvarchar(max)");
        builder.Property(x => x.MetaTitleEn).HasMaxLength(200);
        builder.Property(x => x.MetaTitleBn).HasMaxLength(200);
        builder.Property(x => x.MetaDescriptionEn).HasMaxLength(400);
        builder.Property(x => x.MetaDescriptionBn).HasMaxLength(400);
        builder.HasIndex(x => x.Key).IsUnique();
    }
}

internal sealed class BrandProfileConfiguration : IEntityTypeConfiguration<BrandProfile>
{
    public void Configure(EntityTypeBuilder<BrandProfile> builder)
    {
        builder.ToTable("BrandProfiles");
        builder.Property(x => x.BusinessName).HasMaxLength(120).IsRequired();
        builder.Property(x => x.LogoFileRef).HasMaxLength(300);
        builder.Property(x => x.AddressEn).HasMaxLength(500);
        builder.Property(x => x.AddressBn).HasMaxLength(500);
        builder.Property(x => x.Phone).HasMaxLength(30);
        builder.Property(x => x.WhatsApp).HasMaxLength(30);
        builder.Property(x => x.Email).HasMaxLength(254);
        builder.Property(x => x.WorkingHoursEn).HasMaxLength(300);
        builder.Property(x => x.WorkingHoursBn).HasMaxLength(300);
        builder.Property(x => x.Latitude).HasColumnType("decimal(9,6)");
        builder.Property(x => x.Longitude).HasColumnType("decimal(9,6)");
    }
}

internal sealed class SocialLinkConfiguration : IEntityTypeConfiguration<SocialLink>
{
    public void Configure(EntityTypeBuilder<SocialLink> builder)
    {
        builder.ToTable("SocialLinks");
        builder.Property(x => x.Platform).HasConversion<int>();
        builder.Property(x => x.Url).HasMaxLength(500).IsRequired();
        builder.HasIndex(x => x.Platform).IsUnique();
    }
}

internal sealed class TrustBadgeConfiguration : IEntityTypeConfiguration<TrustBadge>
{
    public void Configure(EntityTypeBuilder<TrustBadge> builder)
    {
        builder.ToTable("TrustBadges");
        builder.Property(x => x.Key).HasMaxLength(40).IsRequired();
        builder.Property(x => x.TextEn).HasMaxLength(160);
        builder.Property(x => x.TextBn).HasMaxLength(160);
        builder.HasIndex(x => x.Key).IsUnique();
    }
}

internal sealed class ContactMessageConfiguration : IEntityTypeConfiguration<ContactMessage>
{
    public void Configure(EntityTypeBuilder<ContactMessage> builder)
    {
        builder.ToTable("ContactMessages");
        builder.Property(x => x.Name).HasMaxLength(120).IsRequired();
        builder.Property(x => x.Mobile).HasMaxLength(30);
        builder.Property(x => x.Email).HasMaxLength(254);
        builder.Property(x => x.Subject).HasMaxLength(200);
        builder.Property(x => x.Message).HasMaxLength(4000).IsRequired();
        builder.Property(x => x.Status).HasConversion<int>();
        builder.Property(x => x.SourcePage).HasMaxLength(200);
        builder.Property(x => x.IpHash).HasMaxLength(64);
        builder.Property(x => x.InternalNote).HasMaxLength(ContactMessage.InternalNoteMax);
        builder.Ignore(x => x.Reference);
        builder.HasIndex(x => new { x.Status, x.ReceivedAt });
        builder.HasIndex(x => new { x.IpHash, x.ReceivedAt });
        builder.HasIndex(x => new { x.Mobile, x.ReceivedAt });
        builder.HasIndex(x => new { x.Email, x.ReceivedAt });
    }
}
