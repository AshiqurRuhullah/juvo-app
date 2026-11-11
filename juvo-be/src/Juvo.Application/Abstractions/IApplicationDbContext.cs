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
using Juvo.Domain.Billing;
using Juvo.Domain.Payments;
using Juvo.Domain.Returns;
using Juvo.Domain.Couriers;
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

    DbSet<Payment> Payments { get; }

    DbSet<PaymentEvent> PaymentEvents { get; }

    DbSet<Refund> Refunds { get; }

    DbSet<SandboxGatewaySession> SandboxGatewaySessions { get; }

    DbSet<TaxRule> TaxRules { get; }

    DbSet<Invoice> Invoices { get; }

    DbSet<CreditNote> CreditNotes { get; }

    DbSet<DocumentSequence> DocumentSequences { get; }

    DbSet<NotificationTemplate> NotificationTemplates { get; }

    DbSet<NotificationTemplateVersion> NotificationTemplateVersions { get; }

    DbSet<Courier> Couriers { get; }

    DbSet<Shipment> Shipments { get; }

    DbSet<ShipmentEvent> ShipmentEvents { get; }

    DbSet<CodSettlement> CodSettlements { get; }

    DbSet<CodSettlementItem> CodSettlementItems { get; }

    DbSet<SandboxConsignment> SandboxConsignments { get; }

    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Takes a named exclusive lock that lasts until the current transaction ends (SQL Server application lock). Other transactions asking for the same name wait. Used to
    /// serialise work that must happen once and in order, such as issuing the invoice of an order or taking the next document number. Needs a running transaction.
    /// </summary>
    Task AcquireTransactionLockAsync(string name, CancellationToken cancellationToken = default);

    /// <summary>Stops tracking everything that was added but not saved. For the path where a save failed on a unique index and the caller must carry on with the context (for example to cancel an external booking).</summary>
    void DiscardUnsavedAdditions();
}
