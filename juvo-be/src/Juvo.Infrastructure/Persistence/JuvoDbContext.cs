using Juvo.Application.Abstractions;
using Juvo.Domain.Carts;
using Juvo.Domain.Delivery;
using Juvo.Domain.Branches;
using Juvo.Domain.Catalog;
using Juvo.Domain.Customers;
using Juvo.Domain.Inventory;
using Juvo.Domain.Identity;
using Juvo.Domain.Notifications;
using Juvo.Domain.Orders;
using Juvo.Domain.Billing;
using Juvo.Domain.Payments;
using Juvo.Domain.Returns;
using Juvo.Domain.Couriers;
using Juvo.Domain.Accounting;
using Juvo.Domain.Purchasing;
using Juvo.Domain.Support;
using Juvo.Domain.Content;
using Juvo.Domain.Marketing;
using Juvo.Domain.Platform;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace Juvo.Infrastructure.Persistence;

/// <summary>
/// The platform database. Staff accounts use ASP.NET Core Identity (no Identity roles: roles, permissions and branch
/// assignments are our own tables). Branch-scoped entities are filtered by the current request's branch scope.
/// </summary>
public sealed class JuvoDbContext : IdentityUserContext<StaffUser, Guid>, IApplicationDbContext
{
    private readonly IBranchContext _branchContext;

    public JuvoDbContext(DbContextOptions<JuvoDbContext> options, IBranchContext? branchContext = null)
        : base(options)
    {
        _branchContext = branchContext ?? UnscopedBranchContext.Instance;
    }

    public DbSet<Role> Roles => Set<Role>();

    public DbSet<Permission> Permissions => Set<Permission>();

    public DbSet<RolePermission> RolePermissions => Set<RolePermission>();

    public DbSet<UserBranchRole> UserBranchRoles => Set<UserBranchRole>();

    public DbSet<Branch> Branches => Set<Branch>();

    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();

    public DbSet<SignInHistory> SignInHistory => Set<SignInHistory>();

    public DbSet<Customer> Customers => Set<Customer>();

    public DbSet<OtpCode> OtpCodes => Set<OtpCode>();

    public DbSet<NotificationLog> NotificationLogs => Set<NotificationLog>();

    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();

    public DbSet<Category> Categories => Set<Category>();

    public DbSet<Brand> Brands => Set<Brand>();

    public DbSet<SizeChart> SizeCharts => Set<SizeChart>();

    public DbSet<Product> Products => Set<Product>();

    public DbSet<ProductVariant> ProductVariants => Set<ProductVariant>();

    public DbSet<ProductImage> ProductImages => Set<ProductImage>();

    public DbSet<StockLevel> StockLevels => Set<StockLevel>();

    public DbSet<StockMovement> StockMovements => Set<StockMovement>();

    public DbSet<StockTransfer> StockTransfers => Set<StockTransfer>();

    public DbSet<StockCount> StockCounts => Set<StockCount>();

    public DbSet<Country> Countries => Set<Country>();

    public DbSet<Division> Divisions => Set<Division>();

    public DbSet<District> Districts => Set<District>();

    public DbSet<Upazila> Upazilas => Set<Upazila>();

    public DbSet<DeliveryZone> DeliveryZones => Set<DeliveryZone>();

    public DbSet<Address> Addresses => Set<Address>();

    public DbSet<WishlistItem> WishlistItems => Set<WishlistItem>();

    public DbSet<ConsentLog> ConsentLogs => Set<ConsentLog>();

    public DbSet<Cart> Carts => Set<Cart>();

    public DbSet<Coupon> Coupons => Set<Coupon>();

    public DbSet<Juvo.Domain.Promotions.Promotion> Promotions => Set<Juvo.Domain.Promotions.Promotion>();

    public DbSet<Juvo.Domain.Orders.OrderItemAdjustment> OrderItemAdjustments => Set<Juvo.Domain.Orders.OrderItemAdjustment>();

    public DbSet<Order> Orders => Set<Order>();

    public DbSet<OrderItem> OrderItems => Set<OrderItem>();

    public DbSet<OrderStatusHistory> OrderStatusHistory => Set<OrderStatusHistory>();

    public DbSet<OrderNote> OrderNotes => Set<OrderNote>();

    public DbSet<OrderRiskFlag> OrderRiskFlags => Set<OrderRiskFlag>();

    public DbSet<FraudReviewLog> FraudReviewLogs => Set<FraudReviewLog>();

    public DbSet<OrderSequence> OrderSequences => Set<OrderSequence>();

    public DbSet<BlacklistEntry> BlacklistEntries => Set<BlacklistEntry>();

    public DbSet<PlatformSetting> PlatformSettings => Set<PlatformSetting>();

    public DbSet<ReturnRequest> ReturnRequests => Set<ReturnRequest>();

    public DbSet<ReturnItem> ReturnItems => Set<ReturnItem>();

    public DbSet<ReturnStatusHistory> ReturnStatusHistory => Set<ReturnStatusHistory>();

    public DbSet<ReturnNote> ReturnNotes => Set<ReturnNote>();

    public DbSet<ReturnPhoto> ReturnPhotos => Set<ReturnPhoto>();

    public DbSet<ReturnRefund> ReturnRefunds => Set<ReturnRefund>();

    public DbSet<ReturnSequence> ReturnSequences => Set<ReturnSequence>();

    public DbSet<Payment> Payments => Set<Payment>();

    public DbSet<PaymentEvent> PaymentEvents => Set<PaymentEvent>();

    public DbSet<Refund> Refunds => Set<Refund>();

    public DbSet<SandboxGatewaySession> SandboxGatewaySessions => Set<SandboxGatewaySession>();

    public DbSet<Courier> Couriers => Set<Courier>();

    public DbSet<Shipment> Shipments => Set<Shipment>();

    public DbSet<ShipmentEvent> ShipmentEvents => Set<ShipmentEvent>();

    public DbSet<CodSettlement> CodSettlements => Set<CodSettlement>();

    public DbSet<CodSettlementItem> CodSettlementItems => Set<CodSettlementItem>();

    public DbSet<SandboxConsignment> SandboxConsignments => Set<SandboxConsignment>();

    public DbSet<FinanceAccount> FinanceAccounts => Set<FinanceAccount>();

    public DbSet<LedgerEntry> LedgerEntries => Set<LedgerEntry>();

    public DbSet<ExpenseCategory> ExpenseCategories => Set<ExpenseCategory>();

    public DbSet<Expense> Expenses => Set<Expense>();

    public DbSet<RecurringExpense> RecurringExpenses => Set<RecurringExpense>();

    public DbSet<Supplier> Suppliers => Set<Supplier>();

    public DbSet<PurchaseOrder> PurchaseOrders => Set<PurchaseOrder>();

    public DbSet<PurchaseOrderLine> PurchaseOrderLines => Set<PurchaseOrderLine>();

    public DbSet<GoodsReceipt> GoodsReceipts => Set<GoodsReceipt>();

    public DbSet<GoodsReceiptLine> GoodsReceiptLines => Set<GoodsReceiptLine>();

    public DbSet<PurchasePayment> PurchasePayments => Set<PurchasePayment>();

    public DbSet<GlobalSequence> GlobalSequences => Set<GlobalSequence>();

    public DbSet<Ticket> Tickets => Set<Ticket>();

    public DbSet<TicketMessage> TicketMessages => Set<TicketMessage>();

    public DbSet<TicketAttachment> TicketAttachments => Set<TicketAttachment>();

    public DbSet<TicketStatusHistory> TicketStatusHistories => Set<TicketStatusHistory>();

    public DbSet<CannedReply> CannedReplies => Set<CannedReply>();

    public DbSet<Juvo.Domain.Loyalty.LoyaltyAccount> LoyaltyAccounts => Set<Juvo.Domain.Loyalty.LoyaltyAccount>();

    public DbSet<Juvo.Domain.Loyalty.PointsLedgerEntry> PointsLedgerEntries => Set<Juvo.Domain.Loyalty.PointsLedgerEntry>();

    public DbSet<Juvo.Domain.Loyalty.CreditLedgerEntry> CreditLedgerEntries => Set<Juvo.Domain.Loyalty.CreditLedgerEntry>();

    public DbSet<Juvo.Domain.Loyalty.OrderTender> OrderTenders => Set<Juvo.Domain.Loyalty.OrderTender>();

    public DbSet<Juvo.Domain.Loyalty.GiftCard> GiftCards => Set<Juvo.Domain.Loyalty.GiftCard>();

    public DbSet<Juvo.Domain.Loyalty.GiftCardEntry> GiftCardEntries => Set<Juvo.Domain.Loyalty.GiftCardEntry>();

    public DbSet<Juvo.Domain.Loyalty.ReferralCode> ReferralCodes => Set<Juvo.Domain.Loyalty.ReferralCode>();

    public DbSet<Juvo.Domain.Loyalty.Referral> Referrals => Set<Juvo.Domain.Loyalty.Referral>();
    public DbSet<CustomerSegment> CustomerSegments => Set<CustomerSegment>();

    public DbSet<ContentPage> ContentPages => Set<ContentPage>();

    public DbSet<BrandProfile> BrandProfiles => Set<BrandProfile>();

    public DbSet<SocialLink> SocialLinks => Set<SocialLink>();

    public DbSet<TrustBadge> TrustBadges => Set<TrustBadge>();

    public DbSet<Campaign> Campaigns => Set<Campaign>();

    public DbSet<CampaignRecipient> CampaignRecipients => Set<CampaignRecipient>();

    public DbSet<Automation> Automations => Set<Automation>();

    public DbSet<BackInStockSubscription> BackInStockSubscriptions => Set<BackInStockSubscription>();

    public DbSet<TaxRule> TaxRules => Set<TaxRule>();

    public DbSet<Invoice> Invoices => Set<Invoice>();

    public DbSet<CreditNote> CreditNotes => Set<CreditNote>();

    public DbSet<DocumentSequence> DocumentSequences => Set<DocumentSequence>();

    public DbSet<NotificationTemplate> NotificationTemplates => Set<NotificationTemplate>();

    public DbSet<NotificationTemplateVersion> NotificationTemplateVersions => Set<NotificationTemplateVersion>();

    public DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();

    public DbSet<JobLog> JobLogs => Set<JobLog>();

    public void DiscardUnsavedAdditions()
    {
        foreach (var entry in ChangeTracker.Entries().Where(e => e.State == EntityState.Added).ToList())
        {
            entry.State = EntityState.Detached;
        }
    }

    public async Task AcquireTransactionLockAsync(string name, CancellationToken cancellationToken = default)
    {
        if (Database.CurrentTransaction is null)
        {
            throw new InvalidOperationException("A transaction lock can only be taken inside a transaction.");
        }

        var result = new Microsoft.Data.SqlClient.SqlParameter("@result", System.Data.SqlDbType.Int) { Direction = System.Data.ParameterDirection.Output };
        var resource = new Microsoft.Data.SqlClient.SqlParameter("@resource", name.Length > 200 ? name[..200] : name);
        await Database.ExecuteSqlRawAsync(
            "EXEC @result = sp_getapplock @Resource = @resource, @LockMode = 'Exclusive', @LockOwner = 'Transaction', @LockTimeout = 20000", [result, resource], cancellationToken);
        if (result.Value is not int code || code < 0)
        {
            throw new TimeoutException($"Could not take the lock '{name}'. Another request is holding it.");
        }
    }

    // Read by the branch-scope query filter on every query (see ModelDefaults.ApplyBranchScope).
    private bool EnforceBranchScope => _branchContext.EnforceScope;

    private Guid[] AllowedBranches => [.. _branchContext.AllowedBranchIds];

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        builder.Entity<StaffUser>().ToTable("StaffUsers");
        builder.Entity<IdentityUserClaim<Guid>>().ToTable("StaffUserClaims");
        builder.Entity<IdentityUserLogin<Guid>>().ToTable("StaffUserLogins");
        builder.Entity<IdentityUserToken<Guid>>().ToTable("StaffUserTokens");

        builder.ApplyConfigurationsFromAssembly(typeof(JuvoDbContext).Assembly);
        ModelDefaults.Apply(builder);
        ModelDefaults.ApplyBranchScope(builder, () => EnforceBranchScope, () => AllowedBranches);
    }

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        base.ConfigureConventions(configurationBuilder);
        ModelDefaults.ConfigureConventions(configurationBuilder);
    }
}
