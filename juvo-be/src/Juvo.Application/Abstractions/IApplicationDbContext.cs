using Juvo.Domain.Carts;
using Juvo.Domain.Delivery;
using Juvo.Domain.Branches;
using Juvo.Domain.Catalog;
using Juvo.Domain.Customers;
using Juvo.Domain.Inventory;
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

    DbSet<Category> Categories { get; }

    DbSet<Brand> Brands { get; }

    DbSet<SizeChart> SizeCharts { get; }

    DbSet<Product> Products { get; }

    DbSet<ProductVariant> ProductVariants { get; }

    DbSet<ProductImage> ProductImages { get; }

    DbSet<StockLevel> StockLevels { get; }

    DbSet<StockMovement> StockMovements { get; }

    DbSet<StockTransfer> StockTransfers { get; }

    DbSet<StockCount> StockCounts { get; }

    DbSet<Country> Countries { get; }

    DbSet<Division> Divisions { get; }

    DbSet<District> Districts { get; }

    DbSet<Upazila> Upazilas { get; }

    DbSet<DeliveryZone> DeliveryZones { get; }

    DbSet<Address> Addresses { get; }

    DbSet<WishlistItem> WishlistItems { get; }

    DbSet<ConsentLog> ConsentLogs { get; }

    DbSet<Cart> Carts { get; }

    DbSet<Coupon> Coupons { get; }

    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);
}
