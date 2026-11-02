using Juvo.Domain.Branches;
using Juvo.Domain.Customers;
using Juvo.Domain.Identity;
using Juvo.Domain.Notifications;
using Juvo.Domain.Platform;
using Microsoft.EntityFrameworkCore;

namespace Juvo.Application.Abstractions;

/// <summary>The data the use cases read and write. Implemented by the EF Core context in Infrastructure.</summary>
public interface IApplicationDbContext
{
    DbSet<StaffUser> Users { get; }

    DbSet<Role> Roles { get; }

    DbSet<Permission> Permissions { get; }

    DbSet<RolePermission> RolePermissions { get; }

    DbSet<UserBranchRole> UserBranchRoles { get; }

    DbSet<Branch> Branches { get; }

    DbSet<RefreshToken> RefreshTokens { get; }

    DbSet<SignInHistory> SignInHistory { get; }

    DbSet<Customer> Customers { get; }

    DbSet<OtpCode> OtpCodes { get; }

    DbSet<NotificationLog> NotificationLogs { get; }

    DbSet<AuditLog> AuditLogs { get; }

    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);
}
