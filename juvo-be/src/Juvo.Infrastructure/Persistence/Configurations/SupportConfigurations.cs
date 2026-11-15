using Juvo.Domain.Branches;
using Juvo.Domain.Customers;
using Juvo.Domain.Orders;
using Juvo.Domain.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Juvo.Infrastructure.Persistence.Configurations;

internal sealed class TicketConfiguration : IEntityTypeConfiguration<Ticket>
{
    public void Configure(EntityTypeBuilder<Ticket> builder)
    {
        builder.ToTable("Tickets");
        builder.Property(x => x.Number).HasMaxLength(30).IsRequired();
        builder.Property(x => x.Channel).HasConversion<int>();
        builder.Property(x => x.Priority).HasConversion<int>();
        builder.Property(x => x.Status).HasConversion<int>();
        builder.Property(x => x.Subject).HasMaxLength(200).IsRequired();
        builder.Property(x => x.Category).HasMaxLength(30).IsRequired();
        builder.Property(x => x.CustomerMobile).HasMaxLength(20).IsRequired();
        builder.Property(x => x.CustomerName).HasMaxLength(120);
        builder.Property(x => x.OrderNumber).HasMaxLength(40);
        builder.Property(x => x.ReturnNumber).HasMaxLength(40);
        builder.Property(x => x.AssigneeName).HasMaxLength(120);
        builder.HasIndex(x => x.Number).IsUnique();
        builder.HasIndex(x => new { x.Status, x.Priority, x.CreatedAt });
        builder.HasIndex(x => new { x.AssigneeId, x.Status });
        builder.HasIndex(x => new { x.BranchId, x.Status });
        builder.HasIndex(x => x.CustomerMobile);
        builder.HasIndex(x => x.CustomerId);
        builder.HasIndex(x => x.OrderId);
        builder.HasIndex(x => new { x.Status, x.ResolvedAt });
        builder.HasOne<Branch>().WithMany().HasForeignKey(x => x.BranchId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Customer>().WithMany().HasForeignKey(x => x.CustomerId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Order>().WithMany().HasForeignKey(x => x.OrderId).OnDelete(DeleteBehavior.Restrict);
        builder.HasMany(x => x.Messages).WithOne().HasForeignKey(m => m.TicketId).OnDelete(DeleteBehavior.Cascade);
        builder.HasMany(x => x.History).WithOne().HasForeignKey(h => h.TicketId).OnDelete(DeleteBehavior.Cascade);
        builder.Navigation(x => x.Messages).UsePropertyAccessMode(PropertyAccessMode.Field);
        builder.Navigation(x => x.History).UsePropertyAccessMode(PropertyAccessMode.Field);
        builder.Ignore(x => x.DomainEvents);
    }
}

internal sealed class TicketMessageConfiguration : IEntityTypeConfiguration<TicketMessage>
{
    public void Configure(EntityTypeBuilder<TicketMessage> builder)
    {
        builder.ToTable("TicketMessages");
        builder.Property(x => x.Kind).HasConversion<int>();
        builder.Property(x => x.Text).HasMaxLength(4000).IsRequired();
        builder.Property(x => x.AuthorName).HasMaxLength(120);
        builder.HasIndex(x => new { x.TicketId, x.At });
        builder.HasMany(x => x.Attachments).WithOne().HasForeignKey(a => a.MessageId).OnDelete(DeleteBehavior.Cascade);
        builder.Navigation(x => x.Attachments).UsePropertyAccessMode(PropertyAccessMode.Field);
    }
}

internal sealed class TicketAttachmentConfiguration : IEntityTypeConfiguration<TicketAttachment>
{
    public void Configure(EntityTypeBuilder<TicketAttachment> builder)
    {
        builder.ToTable("TicketAttachments");
        builder.Property(x => x.Path).HasMaxLength(300).IsRequired();
        builder.Property(x => x.FileName).HasMaxLength(200).IsRequired();
        builder.Property(x => x.ContentType).HasMaxLength(100).IsRequired();
        builder.HasIndex(x => x.MessageId);
    }
}

internal sealed class TicketStatusHistoryConfiguration : IEntityTypeConfiguration<TicketStatusHistory>
{
    public void Configure(EntityTypeBuilder<TicketStatusHistory> builder)
    {
        builder.ToTable("TicketStatusHistory");
        builder.Property(x => x.FromStatus).HasConversion<int?>();
        builder.Property(x => x.ToStatus).HasConversion<int>();
        builder.Property(x => x.Reason).HasMaxLength(300);
        builder.Property(x => x.ActorName).HasMaxLength(120);
        builder.HasIndex(x => new { x.TicketId, x.At });
    }
}

internal sealed class CannedReplyConfiguration : IEntityTypeConfiguration<CannedReply>
{
    public void Configure(EntityTypeBuilder<CannedReply> builder)
    {
        builder.ToTable("CannedReplies");
        builder.Property(x => x.Title).HasMaxLength(120).IsRequired();
        builder.Property(x => x.Body).HasMaxLength(2000).IsRequired();
        builder.Property(x => x.Language).HasMaxLength(2).IsRequired();
        builder.Property(x => x.Category).HasMaxLength(60);
        builder.HasIndex(x => new { x.Language, x.Title }).IsUnique();
    }
}
