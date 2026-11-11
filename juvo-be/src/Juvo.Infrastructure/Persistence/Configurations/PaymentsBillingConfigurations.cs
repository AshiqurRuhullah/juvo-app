using Juvo.Domain.Billing;
using Juvo.Domain.Branches;
using Juvo.Domain.Notifications;
using Juvo.Domain.Orders;
using Juvo.Domain.Payments;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Juvo.Infrastructure.Persistence.Configurations;

internal sealed class PaymentConfiguration : IEntityTypeConfiguration<Payment>
{
    public void Configure(EntityTypeBuilder<Payment> builder)
    {
        builder.ToTable("Payments", t => t.HasCheckConstraint("CK_Payments_Amounts", "[Amount] > 0 AND [RefundedAmount] >= 0 AND [RefundPendingAmount] >= 0 AND [RefundedAmount] + [RefundPendingAmount] <= [Amount]"));
        builder.Property(x => x.OrderNumber).HasMaxLength(40).IsRequired();
        builder.Property(x => x.Kind).HasConversion<int>();
        builder.Property(x => x.Status).HasConversion<int>();
        builder.Property(x => x.Method).HasMaxLength(30).IsRequired();
        builder.Property(x => x.Provider).HasMaxLength(30);
        builder.Property(x => x.Currency).HasMaxLength(3).IsRequired();
        builder.Property(x => x.ProviderReference).HasMaxLength(120);
        builder.Property(x => x.ProviderTransactionId).HasMaxLength(120);
        builder.Property(x => x.RedirectUrl).HasMaxLength(500);
        builder.Property(x => x.ReturnUrl).HasMaxLength(500);
        builder.Property(x => x.SubmittedReference).HasMaxLength(80);
        builder.Property(x => x.PayerName).HasMaxLength(120);
        builder.Property(x => x.ProofPath).HasMaxLength(300);
        builder.Property(x => x.ReceivedReference).HasMaxLength(120);
        builder.Property(x => x.FailureReason).HasMaxLength(500);

        builder.HasIndex(x => new { x.OrderId, x.CreatedAt });
        builder.HasIndex(x => new { x.Provider, x.ProviderReference }).IsUnique().HasFilter("[ProviderReference] IS NOT NULL");
        builder.HasIndex(x => new { x.Method, x.SubmittedReference });
        builder.HasIndex(x => new { x.BranchId, x.Status, x.CreatedAt });
        builder.HasIndex(x => x.OrderNumber);

        builder.HasOne<Branch>().WithMany().HasForeignKey(x => x.BranchId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Order>().WithMany().HasForeignKey(x => x.OrderId).OnDelete(DeleteBehavior.Restrict);
        builder.HasMany(x => x.Events).WithOne().HasForeignKey(e => e.PaymentId).OnDelete(DeleteBehavior.Cascade);
        builder.Navigation(x => x.Events).UsePropertyAccessMode(PropertyAccessMode.Field);
        builder.Ignore(x => x.HoldsMoney);
        builder.Ignore(x => x.Refundable);
    }
}

internal sealed class PaymentEventConfiguration : IEntityTypeConfiguration<PaymentEvent>
{
    public void Configure(EntityTypeBuilder<PaymentEvent> builder)
    {
        builder.ToTable("PaymentEvents");
        builder.Property(x => x.Type).HasMaxLength(40).IsRequired();
        builder.Property(x => x.FromStatus).HasConversion<int?>();
        builder.Property(x => x.ToStatus).HasConversion<int?>();
        builder.Property(x => x.ProviderEventId).HasMaxLength(150);
        builder.Property(x => x.Detail).HasMaxLength(500);
        builder.Property(x => x.ActorName).HasMaxLength(120);
        builder.HasIndex(x => new { x.PaymentId, x.At });
        builder.HasIndex(x => x.ProviderEventId).IsUnique().HasFilter("[ProviderEventId] IS NOT NULL");
    }
}

internal sealed class RefundConfiguration : IEntityTypeConfiguration<Refund>
{
    public void Configure(EntityTypeBuilder<Refund> builder)
    {
        builder.ToTable("Refunds", t => t.HasCheckConstraint("CK_Refunds_Amount", "[Amount] > 0"));
        builder.Property(x => x.Currency).HasMaxLength(3).IsRequired();
        builder.Property(x => x.Mode).HasConversion<int>();
        builder.Property(x => x.Status).HasConversion<int>();
        builder.Property(x => x.Reason).HasMaxLength(500);
        builder.Property(x => x.Reference).HasMaxLength(120);
        builder.Property(x => x.FailureReason).HasMaxLength(500);
        builder.Property(x => x.ActorName).HasMaxLength(120);
        builder.HasIndex(x => new { x.PaymentId, x.Status });
        builder.HasIndex(x => new { x.BranchId, x.Status, x.RequestedAt });
        builder.HasIndex(x => x.OrderId);
        builder.HasIndex(x => x.ReturnRequestId);
        builder.HasOne<Payment>().WithMany().HasForeignKey(x => x.PaymentId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Branch>().WithMany().HasForeignKey(x => x.BranchId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class SandboxGatewaySessionConfiguration : IEntityTypeConfiguration<SandboxGatewaySession>
{
    public void Configure(EntityTypeBuilder<SandboxGatewaySession> builder)
    {
        builder.ToTable("SandboxGatewaySessions");
        builder.Property(x => x.SessionId).HasMaxLength(64).IsRequired();
        builder.Property(x => x.OrderNumber).HasMaxLength(40).IsRequired();
        builder.Property(x => x.Currency).HasMaxLength(3).IsRequired();
        builder.HasIndex(x => x.SessionId).IsUnique();
    }
}

internal sealed class TaxRuleConfiguration : IEntityTypeConfiguration<TaxRule>
{
    public void Configure(EntityTypeBuilder<TaxRule> builder)
    {
        builder.ToTable("TaxRules", t => t.HasCheckConstraint("CK_TaxRules_Rate", "[RatePercent] >= 0 AND [RatePercent] <= 100"));
        builder.Property(x => x.Code).HasMaxLength(30).IsRequired();
        builder.Property(x => x.Name).HasMaxLength(120).IsRequired();
        builder.Property(x => x.RatePercent).HasPrecision(5, 2);
        builder.Property(x => x.Note).HasMaxLength(500);
        builder.HasIndex(x => x.Code).IsUnique();
    }
}

internal sealed class InvoiceConfiguration : IEntityTypeConfiguration<Invoice>
{
    public void Configure(EntityTypeBuilder<Invoice> builder)
    {
        builder.ToTable("Invoices");
        builder.Property(x => x.Number).HasMaxLength(40).IsRequired();
        builder.Property(x => x.OrderNumber).HasMaxLength(40).IsRequired();
        builder.Property(x => x.CustomerName).HasMaxLength(120).IsRequired();
        builder.Property(x => x.CustomerMobile).HasMaxLength(20).IsRequired();
        builder.Property(x => x.CustomerEmail).HasMaxLength(254);
        builder.Property(x => x.BillingAddress).HasMaxLength(600).IsRequired();
        builder.Property(x => x.SellerName).HasMaxLength(150).IsRequired();
        builder.Property(x => x.SellerAddress).HasMaxLength(400);
        builder.Property(x => x.SellerPhone).HasMaxLength(40);
        builder.Property(x => x.SellerEmail).HasMaxLength(254);
        builder.Property(x => x.SellerTaxId).HasMaxLength(60);
        builder.Property(x => x.Currency).HasMaxLength(3).IsRequired();
        builder.Property(x => x.TaxRuleCode).HasMaxLength(30);
        builder.Property(x => x.TaxRuleName).HasMaxLength(120);
        builder.Property(x => x.TaxRatePercent).HasPrecision(5, 2);
        builder.Property(x => x.IssuedByName).HasMaxLength(120);
        builder.HasIndex(x => x.Number).IsUnique();
        builder.HasIndex(x => x.OrderId).IsUnique();
        builder.HasIndex(x => new { x.BranchId, x.IssuedAt });
        builder.HasOne<Branch>().WithMany().HasForeignKey(x => x.BranchId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Order>().WithMany().HasForeignKey(x => x.OrderId).OnDelete(DeleteBehavior.Restrict);
        builder.HasMany(x => x.Lines).WithOne().HasForeignKey(l => l.InvoiceId).OnDelete(DeleteBehavior.Restrict);
        builder.Navigation(x => x.Lines).UsePropertyAccessMode(PropertyAccessMode.Field);
    }
}

internal sealed class InvoiceLineConfiguration : IEntityTypeConfiguration<InvoiceLine>
{
    public void Configure(EntityTypeBuilder<InvoiceLine> builder)
    {
        builder.ToTable("InvoiceLines");
        builder.Property(x => x.Name).HasMaxLength(200).IsRequired();
        builder.Property(x => x.NameBn).HasMaxLength(200);
        builder.Property(x => x.Sku).HasMaxLength(60).IsRequired();
        builder.Property(x => x.Variant).HasMaxLength(120);
        builder.HasIndex(x => x.InvoiceId);
    }
}

internal sealed class CreditNoteConfiguration : IEntityTypeConfiguration<CreditNote>
{
    public void Configure(EntityTypeBuilder<CreditNote> builder)
    {
        builder.ToTable("CreditNotes");
        builder.Property(x => x.Number).HasMaxLength(40).IsRequired();
        builder.Property(x => x.InvoiceNumber).HasMaxLength(40).IsRequired();
        builder.Property(x => x.OrderNumber).HasMaxLength(40).IsRequired();
        builder.Property(x => x.Currency).HasMaxLength(3).IsRequired();
        builder.Property(x => x.Reason).HasMaxLength(500);
        builder.Property(x => x.IssuedByName).HasMaxLength(120);
        builder.HasIndex(x => x.Number).IsUnique();
        builder.HasIndex(x => x.RefundId).IsUnique();
        builder.HasIndex(x => x.InvoiceId);
        builder.HasIndex(x => new { x.BranchId, x.IssuedAt });
        builder.HasOne<Branch>().WithMany().HasForeignKey(x => x.BranchId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Invoice>().WithMany().HasForeignKey(x => x.InvoiceId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class DocumentSequenceConfiguration : IEntityTypeConfiguration<DocumentSequence>
{
    public void Configure(EntityTypeBuilder<DocumentSequence> builder)
    {
        builder.ToTable("DocumentSequences");
        builder.Property(x => x.Kind).HasMaxLength(4).IsRequired();
        builder.Property(x => x.Period).HasMaxLength(4).IsRequired();
        builder.HasIndex(x => new { x.BranchId, x.Kind, x.Period }).IsUnique();
        builder.HasOne<Branch>().WithMany().HasForeignKey(x => x.BranchId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class NotificationTemplateConfiguration : IEntityTypeConfiguration<NotificationTemplate>
{
    public void Configure(EntityTypeBuilder<NotificationTemplate> builder)
    {
        builder.ToTable("NotificationTemplates");
        builder.Property(x => x.Code).HasMaxLength(60).IsRequired();
        builder.Property(x => x.Channel).HasConversion<int>();
        builder.Property(x => x.Category).HasConversion<int>();
        builder.Property(x => x.Language).HasMaxLength(5).IsRequired();
        builder.Property(x => x.Subject).HasMaxLength(300);
        builder.Property(x => x.Body).HasMaxLength(2000).IsRequired();
        builder.Property(x => x.EditedByName).HasMaxLength(120);
        builder.HasIndex(x => new { x.Code, x.Channel, x.Language }).IsUnique();
    }
}

internal sealed class NotificationTemplateVersionConfiguration : IEntityTypeConfiguration<NotificationTemplateVersion>
{
    public void Configure(EntityTypeBuilder<NotificationTemplateVersion> builder)
    {
        builder.ToTable("NotificationTemplateVersions");
        builder.Property(x => x.Subject).HasMaxLength(300);
        builder.Property(x => x.Body).HasMaxLength(2000).IsRequired();
        builder.Property(x => x.EditedByName).HasMaxLength(120);
        builder.HasIndex(x => new { x.TemplateId, x.Version }).IsUnique();
        builder.HasOne<NotificationTemplate>().WithMany().HasForeignKey(x => x.TemplateId).OnDelete(DeleteBehavior.Cascade);
    }
}
