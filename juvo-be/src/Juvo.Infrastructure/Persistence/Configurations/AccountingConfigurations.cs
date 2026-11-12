using Juvo.Domain.Accounting;
using Juvo.Domain.Branches;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Juvo.Infrastructure.Persistence.Configurations;

internal sealed class FinanceAccountConfiguration : IEntityTypeConfiguration<FinanceAccount>
{
    public void Configure(EntityTypeBuilder<FinanceAccount> builder)
    {
        builder.ToTable("FinanceAccounts");
        builder.Property(x => x.Name).HasMaxLength(120).IsRequired();
        builder.Property(x => x.Type).HasConversion<int>();
        builder.Property(x => x.AccountNumber).HasMaxLength(60);
        builder.Property(x => x.Note).HasMaxLength(300);
        builder.HasIndex(x => new { x.BranchId, x.Name }).IsUnique();
        builder.HasOne<Branch>().WithMany().HasForeignKey(x => x.BranchId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class LedgerEntryConfiguration : IEntityTypeConfiguration<LedgerEntry>
{
    public void Configure(EntityTypeBuilder<LedgerEntry> builder)
    {
        builder.ToTable("LedgerEntries");
        builder.Property(x => x.Kind).HasConversion<int>();
        builder.Property(x => x.Description).HasMaxLength(300).IsRequired();
        builder.Property(x => x.SourceType).HasMaxLength(30);
        builder.Property(x => x.ActorName).HasMaxLength(120);
        builder.HasIndex(x => new { x.AccountId, x.EntryDate });
        builder.HasIndex(x => new { x.BranchId, x.EntryDate });
        builder.HasIndex(x => x.TransferId);

        // One entry per source and kind: approving an expense or recording a payment twice can never book it twice.
        builder.HasIndex(x => new { x.SourceType, x.SourceId, x.Kind }).IsUnique().HasFilter("[SourceId] IS NOT NULL");
        builder.HasOne<FinanceAccount>().WithMany().HasForeignKey(x => x.AccountId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Branch>().WithMany().HasForeignKey(x => x.BranchId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class ExpenseCategoryConfiguration : IEntityTypeConfiguration<ExpenseCategory>
{
    public void Configure(EntityTypeBuilder<ExpenseCategory> builder)
    {
        builder.ToTable("ExpenseCategories");
        builder.Property(x => x.NameEn).HasMaxLength(120).IsRequired();
        builder.Property(x => x.NameBn).HasMaxLength(120);
        builder.HasIndex(x => new { x.ParentId, x.NameEn }).IsUnique();
        builder.HasOne<ExpenseCategory>().WithMany().HasForeignKey(x => x.ParentId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class ExpenseConfiguration : IEntityTypeConfiguration<Expense>
{
    public void Configure(EntityTypeBuilder<Expense> builder)
    {
        builder.ToTable("Expenses", t => t.HasCheckConstraint("CK_Expenses_Amount", "[Amount] > 0"));
        builder.Property(x => x.Number).HasMaxLength(40).IsRequired();
        builder.Property(x => x.Method).HasConversion<int>();
        builder.Property(x => x.Status).HasConversion<int>();
        builder.Property(x => x.Reference).HasMaxLength(120);
        builder.Property(x => x.Notes).HasMaxLength(1000);
        builder.Property(x => x.DecidedByName).HasMaxLength(120);
        builder.Property(x => x.DecisionNote).HasMaxLength(500);
        builder.Property(x => x.RecurringPeriod).HasMaxLength(4);
        builder.HasIndex(x => x.Number).IsUnique();
        builder.HasIndex(x => new { x.BranchId, x.Status, x.ExpenseDate });
        builder.HasIndex(x => new { x.CategoryId, x.ExpenseDate });
        builder.HasIndex(x => x.SupplierId);

        // A recurring template creates one expense per month, once.
        builder.HasIndex(x => new { x.RecurringExpenseId, x.RecurringPeriod }).IsUnique().HasFilter("[RecurringExpenseId] IS NOT NULL");
        builder.HasOne<Branch>().WithMany().HasForeignKey(x => x.BranchId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<ExpenseCategory>().WithMany().HasForeignKey(x => x.CategoryId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<FinanceAccount>().WithMany().HasForeignKey(x => x.AccountId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<RecurringExpense>().WithMany().HasForeignKey(x => x.RecurringExpenseId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Juvo.Domain.Purchasing.Supplier>().WithMany().HasForeignKey(x => x.SupplierId).OnDelete(DeleteBehavior.Restrict);
        builder.HasMany(x => x.Attachments).WithOne().HasForeignKey(a => a.ExpenseId).OnDelete(DeleteBehavior.Cascade);
        builder.Navigation(x => x.Attachments).UsePropertyAccessMode(PropertyAccessMode.Field);
    }
}

internal sealed class ExpenseAttachmentConfiguration : IEntityTypeConfiguration<ExpenseAttachment>
{
    public void Configure(EntityTypeBuilder<ExpenseAttachment> builder)
    {
        builder.ToTable("ExpenseAttachments");
        builder.Property(x => x.Path).HasMaxLength(300).IsRequired();
        builder.Property(x => x.FileName).HasMaxLength(200).IsRequired();
        builder.Property(x => x.ContentType).HasMaxLength(100).IsRequired();
        builder.HasIndex(x => x.ExpenseId);
    }
}

internal sealed class RecurringExpenseConfiguration : IEntityTypeConfiguration<RecurringExpense>
{
    public void Configure(EntityTypeBuilder<RecurringExpense> builder)
    {
        builder.ToTable("RecurringExpenses", t => t.HasCheckConstraint("CK_RecurringExpenses_Day", "[DayOfMonth] BETWEEN 1 AND 28 AND [Amount] > 0"));
        builder.Property(x => x.Title).HasMaxLength(160).IsRequired();
        builder.Property(x => x.Method).HasConversion<int>();
        builder.Property(x => x.Notes).HasMaxLength(1000);
        builder.Property(x => x.LastGeneratedPeriod).HasMaxLength(4);
        builder.HasIndex(x => new { x.BranchId, x.IsActive });
        builder.HasOne<Branch>().WithMany().HasForeignKey(x => x.BranchId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<ExpenseCategory>().WithMany().HasForeignKey(x => x.CategoryId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<FinanceAccount>().WithMany().HasForeignKey(x => x.AccountId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Juvo.Domain.Purchasing.Supplier>().WithMany().HasForeignKey(x => x.SupplierId).OnDelete(DeleteBehavior.Restrict);
    }
}
