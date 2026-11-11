using Juvo.Domain.Carts;
using Juvo.Domain.Delivery;
using Juvo.Domain.Branches;
using Juvo.Domain.Catalog;
using Juvo.Domain.Customers;
using Juvo.Domain.Inventory;
using Juvo.Domain.Identity;
using Juvo.Domain.Notifications;
using Juvo.Domain.Orders;
using Juvo.Domain.Platform;
using Juvo.Domain.Returns;
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

    DbSet<Order> Orders { get; }

    DbSet<OrderItem> OrderItems { get; }

    DbSet<OrderStatusHistory> OrderStatusHistory { get; }

    DbSet<OrderNote> OrderNotes { get; }

    DbSet<OrderRiskFlag> OrderRiskFlags { get; }

    DbSet<FraudReviewLog> FraudReviewLogs { get; }

    DbSet<OrderSequence> OrderSequences { get; }

    DbSet<BlacklistEntry> BlacklistEntries { get; }

    DbSet<PlatformSetting> PlatformSettings { get; }

    DbSet<ReturnRequest> ReturnRequests { get; }

    DbSet<ReturnItem> ReturnItems { get; }

    DbSet<ReturnStatusHistory> ReturnStatusHistory { get; }

    DbSet<ReturnNote> ReturnNotes { get; }

    DbSet<ReturnPhoto> ReturnPhotos { get; }

    DbSet<ReturnRefund> ReturnRefunds { get; }

    DbSet<ReturnSequence> ReturnSequences { get; }

    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);
}
