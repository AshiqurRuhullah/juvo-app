using Juvo.Domain.Branches;
using Juvo.Domain.Customers;
using Juvo.Domain.Identity;
using Juvo.Domain.Notifications;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Juvo.Infrastructure.Persistence.Configurations;

internal sealed class StaffUserConfiguration : IEntityTypeConfiguration<StaffUser>
{
    public void Configure(EntityTypeBuilder<StaffUser> builder)
    {
        builder.Property(x => x.FullName).HasMaxLength(150).IsRequired();
        builder.HasIndex(x => x.IsActive);
        builder.HasIndex(x => x.IsDeleted);
    }
}

internal sealed class RoleConfiguration : IEntityTypeConfiguration<Role>
{
    public void Configure(EntityTypeBuilder<Role> builder)
    {
        builder.ToTable("Roles");
        builder.Property(x => x.Name).HasMaxLength(100).IsRequired();
        builder.Property(x => x.Description).HasMaxLength(500);
        builder.HasIndex(x => x.Name).IsUnique();
    }
}

internal sealed class PermissionConfiguration : IEntityTypeConfiguration<Permission>
{
    public void Configure(EntityTypeBuilder<Permission> builder)
    {
        builder.ToTable("Permissions");
        builder.Property(x => x.Code).HasMaxLength(100).IsRequired();
        builder.Property(x => x.Module).HasMaxLength(50).IsRequired();
        builder.Property(x => x.Resource).HasMaxLength(50).IsRequired();
        builder.Property(x => x.Action).HasMaxLength(50).IsRequired();
        builder.Property(x => x.Description).HasMaxLength(200).IsRequired();
        builder.HasIndex(x => x.Code).IsUnique();
        builder.HasIndex(x => x.Module);
    }
}

internal sealed class RolePermissionConfiguration : IEntityTypeConfiguration<RolePermission>
{
    public void Configure(EntityTypeBuilder<RolePermission> builder)
    {
        builder.ToTable("RolePermissions");
        builder.HasIndex(x => new { x.RoleId, x.PermissionId }).IsUnique();
        builder.HasOne<Role>().WithMany().HasForeignKey(x => x.RoleId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Permission>().WithMany().HasForeignKey(x => x.PermissionId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class UserBranchRoleConfiguration : IEntityTypeConfiguration<UserBranchRole>
{
    public void Configure(EntityTypeBuilder<UserBranchRole> builder)
    {
        builder.ToTable("UserBranchRoles");

        // BranchId = null means "all branches" (global scope). SQL Server allows one NULL per unique key, which is what we want.
        builder.HasIndex(x => new { x.UserId, x.BranchId, x.RoleId }).IsUnique();
        builder.HasIndex(x => x.BranchId);
        builder.HasIndex(x => x.RoleId);
        builder.HasOne<StaffUser>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Branch>().WithMany().HasForeignKey(x => x.BranchId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Role>().WithMany().HasForeignKey(x => x.RoleId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class BranchConfiguration : IEntityTypeConfiguration<Branch>
{
    public void Configure(EntityTypeBuilder<Branch> builder)
    {
        builder.ToTable("Branches", t =>
        {
            t.HasCheckConstraint("CK_Branches_Latitude", "[Latitude] IS NULL OR ([Latitude] >= -90 AND [Latitude] <= 90)");
            t.HasCheckConstraint("CK_Branches_Longitude", "[Longitude] IS NULL OR ([Longitude] >= -180 AND [Longitude] <= 180)");
        });
        builder.Property(x => x.Code).HasMaxLength(20).IsRequired();
        builder.Property(x => x.NameEn).HasMaxLength(150).IsRequired();
        builder.Property(x => x.NameBn).HasMaxLength(150);
        builder.Property(x => x.Type).HasConversion<int>();
        builder.Property(x => x.AddressLine).HasMaxLength(300);
        builder.Property(x => x.Phone).HasMaxLength(40);
        builder.Property(x => x.Email).HasMaxLength(200);
        builder.Property(x => x.WorkingHours).HasMaxLength(300);
        builder.Property(x => x.TimeZoneId).HasMaxLength(60).IsRequired();
        builder.Property(x => x.DefaultCurrency).HasMaxLength(3).IsRequired();
        builder.Property(x => x.OrderPrefix).HasMaxLength(10).IsRequired();
        builder.Property(x => x.PickupAddress).HasMaxLength(300);
        builder.Property(x => x.CourierStoreIds).HasMaxLength(1000);
        builder.HasIndex(x => x.Code).IsUnique();
        builder.HasIndex(x => x.OrderPrefix);
        builder.HasIndex(x => x.IsDefault).HasFilter("[IsDefault] = 1").IsUnique(); // exactly one default
        builder.HasOne<Branch>().WithMany().HasForeignKey(x => x.ParentBranchId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class RefreshTokenConfiguration : IEntityTypeConfiguration<RefreshToken>
{
    public void Configure(EntityTypeBuilder<RefreshToken> builder)
    {
        builder.ToTable("RefreshTokens");
        builder.Property(x => x.SubjectType).HasConversion<int>();
        builder.Property(x => x.TokenHash).HasMaxLength(64).IsRequired();
        builder.Property(x => x.RevokedReason).HasMaxLength(60);
        builder.Property(x => x.CreatedByIp).HasMaxLength(64);
        builder.Property(x => x.UserAgent).HasMaxLength(400);
        builder.Property(x => x.DeviceName).HasMaxLength(100);
        builder.HasIndex(x => x.TokenHash).IsUnique();
        builder.HasIndex(x => new { x.SubjectType, x.SubjectId });
        builder.HasIndex(x => x.FamilyId);
    }
}

internal sealed class SignInHistoryConfiguration : IEntityTypeConfiguration<SignInHistory>
{
    public void Configure(EntityTypeBuilder<SignInHistory> builder)
    {
        builder.ToTable("SignInHistory");
        builder.Property(x => x.SubjectType).HasConversion<int>();
        builder.Property(x => x.Identifier).HasMaxLength(200).IsRequired();
        builder.Property(x => x.FailureReason).HasMaxLength(60);
        builder.Property(x => x.IpAddress).HasMaxLength(64);
        builder.Property(x => x.UserAgent).HasMaxLength(400);
        builder.HasIndex(x => new { x.SubjectId, x.OccurredAt });
        builder.HasIndex(x => x.OccurredAt);
    }
}

internal sealed class CustomerConfiguration : IEntityTypeConfiguration<Customer>
{
    public void Configure(EntityTypeBuilder<Customer> builder)
    {
        builder.ToTable("Customers");
        builder.Property(x => x.Mobile).HasMaxLength(20).IsRequired();
        builder.Property(x => x.Email).HasMaxLength(200);
        builder.Property(x => x.FullName).HasMaxLength(150);
        builder.Property(x => x.PreferredLanguage).HasMaxLength(5);
        builder.Property(x => x.ProfileLevel).HasConversion<int>();
        builder.HasIndex(x => x.Mobile).IsUnique(); // the mobile number is the customer key (BR-006)
        builder.HasIndex(x => x.Email);
    }
}

internal sealed class OtpCodeConfiguration : IEntityTypeConfiguration<OtpCode>
{
    public void Configure(EntityTypeBuilder<OtpCode> builder)
    {
        builder.ToTable("OtpCodes");
        builder.Property(x => x.Mobile).HasMaxLength(20).IsRequired();
        builder.Property(x => x.Purpose).HasConversion<int>();
        builder.Property(x => x.CodeHash).HasMaxLength(64).IsRequired();
        builder.Property(x => x.RequestedIp).HasMaxLength(64);
        builder.HasIndex(x => new { x.Mobile, x.CreatedAt });
    }
}

internal sealed class NotificationLogConfiguration : IEntityTypeConfiguration<NotificationLog>
{
    public void Configure(EntityTypeBuilder<NotificationLog> builder)
    {
        builder.ToTable("NotificationLogs");
        builder.Property(x => x.Channel).HasConversion<int>();
        builder.Property(x => x.Status).HasConversion<int>();
        builder.Property(x => x.Recipient).HasMaxLength(200).IsRequired();
        builder.Property(x => x.Template).HasMaxLength(100).IsRequired();
        builder.Property(x => x.Subject).HasMaxLength(300);
        builder.Property(x => x.Body).HasColumnType("nvarchar(max)").IsRequired();
        builder.Property(x => x.ProviderResponse).HasMaxLength(500);
        builder.Property(x => x.CorrelationId).HasMaxLength(100);
        builder.HasIndex(x => new { x.Recipient, x.SentAt });
        builder.HasIndex(x => x.Template);
        builder.Property(x => x.Category).HasConversion<int>();
        builder.Property(x => x.Language).HasMaxLength(5);
        builder.Property(x => x.DedupeKey).HasMaxLength(150);
        builder.Property(x => x.LastError).HasMaxLength(500);
        builder.HasIndex(x => new { x.Status, x.QueuedAt });
        builder.HasIndex(x => new { x.DedupeKey, x.Channel }).IsUnique().HasFilter("[DedupeKey] IS NOT NULL");
    }
}
