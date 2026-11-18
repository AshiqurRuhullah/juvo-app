using Juvo.Domain.Customers;
using Juvo.Domain.Loyalty;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Juvo.Infrastructure.Persistence.Configurations;

internal sealed class LoyaltyAccountConfiguration : IEntityTypeConfiguration<LoyaltyAccount>
{
    public void Configure(EntityTypeBuilder<LoyaltyAccount> builder)
    {
        // The database itself refuses a negative balance: the last line of defence behind the lock and the row version.
        builder.ToTable("LoyaltyAccounts", t => t.HasCheckConstraint("CK_LoyaltyAccounts_Balances", "[PointsBalance] >= 0 AND [CreditBalance] >= 0"));
        builder.HasIndex(x => x.CustomerId).IsUnique();
        builder.HasOne<Customer>().WithMany().HasForeignKey(x => x.CustomerId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class PointsLedgerEntryConfiguration : IEntityTypeConfiguration<PointsLedgerEntry>
{
    public void Configure(EntityTypeBuilder<PointsLedgerEntry> builder)
    {
        builder.ToTable("PointsLedgerEntries", t => t.HasCheckConstraint("CK_PointsLedgerEntries_Balance", "[BalanceAfter] >= 0 AND [Points] <> 0"));
        builder.Property(x => x.Kind).HasConversion<int>();
        builder.Property(x => x.Reason).HasMaxLength(300).IsRequired();
        builder.Property(x => x.ReferenceKey).HasMaxLength(120);
        builder.Property(x => x.OrderNumber).HasMaxLength(40);
        builder.Property(x => x.ActorName).HasMaxLength(120);

        // One posting per customer and key: the same order can never redeem, earn or refund twice.
        builder.HasIndex(x => new { x.CustomerId, x.ReferenceKey }).IsUnique().HasFilter("[ReferenceKey] IS NOT NULL");
        builder.HasIndex(x => new { x.CustomerId, x.At });
        builder.HasIndex(x => new { x.ExpiresAt, x.CustomerId }).HasFilter("[ExpiresAt] IS NOT NULL AND [Points] > 0");
        builder.HasIndex(x => x.OrderId);
        builder.HasOne<Customer>().WithMany().HasForeignKey(x => x.CustomerId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class CreditLedgerEntryConfiguration : IEntityTypeConfiguration<CreditLedgerEntry>
{
    public void Configure(EntityTypeBuilder<CreditLedgerEntry> builder)
    {
        builder.ToTable("CreditLedgerEntries", t => t.HasCheckConstraint("CK_CreditLedgerEntries_Balance", "[BalanceAfter] >= 0 AND [Amount] <> 0"));
        builder.Property(x => x.Kind).HasConversion<int>();
        builder.Property(x => x.Reason).HasMaxLength(300).IsRequired();
        builder.Property(x => x.ReferenceKey).HasMaxLength(120);
        builder.Property(x => x.OrderNumber).HasMaxLength(40);
        builder.Property(x => x.ActorName).HasMaxLength(120);
        builder.HasIndex(x => new { x.CustomerId, x.ReferenceKey }).IsUnique().HasFilter("[ReferenceKey] IS NOT NULL");
        builder.HasIndex(x => new { x.CustomerId, x.At });
        builder.HasIndex(x => x.OrderId);
        builder.HasOne<Customer>().WithMany().HasForeignKey(x => x.CustomerId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class OrderTenderConfiguration : IEntityTypeConfiguration<OrderTender>
{
    public void Configure(EntityTypeBuilder<OrderTender> builder)
    {
        builder.ToTable("OrderTenders", t => t.HasCheckConstraint("CK_OrderTenders_Amounts", "[PointsUsed] >= 0 AND [PointsAmount] >= 0 AND [CreditAmount] >= 0 AND [GiftCardAmount] >= 0 AND [PointsReturned] >= 0 AND [PointsReturned] <= [PointsUsed]"));
        builder.Property(x => x.GiftCardHint).HasMaxLength(8);
        builder.Ignore(x => x.Total);

        // One tender row per order: an order can never redeem twice.
        builder.HasIndex(x => x.OrderId).IsUnique();
        builder.HasIndex(x => x.GiftCardId).HasFilter("[GiftCardId] IS NOT NULL");
    }
}

internal sealed class GiftCardConfiguration : IEntityTypeConfiguration<GiftCard>
{
    public void Configure(EntityTypeBuilder<GiftCard> builder)
    {
        builder.ToTable("GiftCards", t => t.HasCheckConstraint("CK_GiftCards_Balance", "[Balance] >= 0 AND [InitialAmount] > 0 AND [Balance] <= [InitialAmount]"));
        builder.Property(x => x.CodeHash).HasMaxLength(64).IsRequired();
        builder.Property(x => x.CodeHint).HasMaxLength(8).IsRequired();
        builder.Property(x => x.Note).HasMaxLength(300);
        builder.Property(x => x.RecipientMobile).HasMaxLength(20);
        builder.Property(x => x.RecipientEmail).HasMaxLength(254);
        builder.Property(x => x.CreatedByName).HasMaxLength(120);
        builder.HasIndex(x => x.CodeHash).IsUnique();
        builder.HasIndex(x => new { x.IsActive, x.CreatedAt });
    }
}

internal sealed class GiftCardEntryConfiguration : IEntityTypeConfiguration<GiftCardEntry>
{
    public void Configure(EntityTypeBuilder<GiftCardEntry> builder)
    {
        builder.ToTable("GiftCardEntries", t => t.HasCheckConstraint("CK_GiftCardEntries_Balance", "[BalanceAfter] >= 0"));
        builder.Property(x => x.Kind).HasConversion<int>();
        builder.Property(x => x.ReferenceKey).HasMaxLength(120);
        builder.Property(x => x.OrderNumber).HasMaxLength(40);
        builder.Property(x => x.ActorName).HasMaxLength(120);
        builder.Property(x => x.Note).HasMaxLength(300);
        builder.HasIndex(x => new { x.GiftCardId, x.ReferenceKey }).IsUnique().HasFilter("[ReferenceKey] IS NOT NULL");
        builder.HasIndex(x => new { x.GiftCardId, x.At });
        builder.HasOne<GiftCard>().WithMany().HasForeignKey(x => x.GiftCardId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class ReferralCodeConfiguration : IEntityTypeConfiguration<ReferralCode>
{
    public void Configure(EntityTypeBuilder<ReferralCode> builder)
    {
        builder.ToTable("ReferralCodes");
        builder.Property(x => x.Code).HasMaxLength(12).IsRequired();
        builder.HasIndex(x => x.Code).IsUnique();
        builder.HasIndex(x => x.CustomerId).IsUnique();
        builder.HasOne<Customer>().WithMany().HasForeignKey(x => x.CustomerId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class ReferralConfiguration : IEntityTypeConfiguration<Referral>
{
    public void Configure(EntityTypeBuilder<Referral> builder)
    {
        builder.ToTable("Referrals", t => t.HasCheckConstraint("CK_Referrals_NotSelf", "[ReferrerId] <> [RefereeId]"));
        builder.Property(x => x.CodeUsed).HasMaxLength(12).IsRequired();
        builder.Property(x => x.Status).HasConversion<int>();
        builder.Property(x => x.Note).HasMaxLength(300);
        builder.Property(x => x.HouseholdKey).HasMaxLength(64);

        // A customer can be referred only once; no self-referral.
        builder.HasIndex(x => x.RefereeId).IsUnique();
        builder.HasIndex(x => new { x.ReferrerId, x.Status });
        builder.HasIndex(x => x.QualifyingOrderId).HasFilter("[QualifyingOrderId] IS NOT NULL");
        builder.HasIndex(x => x.HouseholdKey).HasFilter("[HouseholdKey] IS NOT NULL");
        builder.HasOne<Customer>().WithMany().HasForeignKey(x => x.ReferrerId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Customer>().WithMany().HasForeignKey(x => x.RefereeId).OnDelete(DeleteBehavior.Restrict);
    }
}
