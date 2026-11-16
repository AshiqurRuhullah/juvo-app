using FluentValidation;
using Juvo.Application.Abstractions;
using Juvo.Application.Catalog;
using Juvo.Application.Identity;
using Juvo.Application.Inventory;
using Juvo.Application.Ums;
using Juvo.Application.Messaging;
using Juvo.Application.Messaging.Behaviors;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Juvo.Application;

public static class DependencyInjection
{
    /// <summary>Registers the dispatcher, pipeline behaviours (logging, permission, validation, transaction), handlers and validators.</summary>
    public static IServiceCollection AddApplication(this IServiceCollection services)
    {
        var assembly = typeof(DependencyInjection).Assembly;

        services.AddScoped<IDispatcher, Dispatcher>();

        // Order matters: first registered runs outermost.
        services.AddScoped(typeof(IPipelineBehavior<,>), typeof(LoggingBehavior<,>));
        services.AddScoped(typeof(IPipelineBehavior<,>), typeof(PermissionBehavior<,>));
        services.AddScoped(typeof(IPipelineBehavior<,>), typeof(ValidationBehavior<,>));
        services.AddScoped(typeof(IPipelineBehavior<,>), typeof(TransactionBehavior<,>));

        foreach (var type in assembly.GetTypes().Where(t => t is { IsAbstract: false, IsInterface: false }))
        {
            foreach (var contract in type.GetInterfaces().Where(i =>
                         i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IRequestHandler<,>)))
            {
                services.AddScoped(contract, type);
            }
        }

        services.AddValidatorsFromAssembly(assembly, includeInternalTypes: true);

        // Identity and access (Step 3).
        services.AddMemoryCache();
        services.AddScoped<IUserStateCache, UserStateCache>();
        services.AddScoped<AccessService>();
        services.AddScoped<IPermissionChecker, PermissionChecker>();
        services.AddScoped<BranchContextResolver>();
        services.AddScoped<SessionIssuer>();
        services.AddScoped<AuthService>();
        services.AddScoped<CustomerAuthService>();

        // User management (module O, section 4A).
        services.AddScoped<ActorContext>();
        services.AddScoped<AccessChanges>();
        services.AddScoped<UsersService>();
        services.AddScoped<RolesService>();
        services.AddScoped<BranchesService>();
        services.AddScoped<AuditQueryService>();

        // Catalog (module B).
        services.AddScoped<CatalogLookupsService>();
        services.AddScoped<ProductsService>();
        services.AddScoped<ProductMediaService>();
        services.AddScoped<ProductImportService>();

        // Storefront, cart, customers, delivery (Step 7).
        services.AddScoped<Juvo.Application.Storefront.StockAvailability>();
        services.AddScoped<Juvo.Application.Storefront.StorefrontCatalogService>();
        services.AddScoped<Juvo.Application.Carts.CartService>();
        services.AddScoped<Juvo.Application.Carts.CouponsService>();
        services.AddScoped<Juvo.Application.Customers.CustomerAccountService>();
        services.AddScoped<Juvo.Application.Customers.WishlistService>();
        services.AddScoped<Juvo.Application.Customers.CustomersAdminService>();
        services.AddScoped<Juvo.Application.Delivery.DeliveryService>();

        // Orders, checkout and fraud basics (Step 8).
        services.AddScoped<Juvo.Application.Orders.SettingsService>();
        services.AddScoped<Juvo.Application.Orders.OrderStock>();
        services.AddScoped<Juvo.Application.Orders.OrderRouter>();
        services.AddScoped<Juvo.Application.Orders.OrderAccess>();
        services.AddScoped<Juvo.Application.Orders.OrderWorkflow>();
        services.AddScoped<Juvo.Application.Orders.FraudService>();
        services.AddScoped<Juvo.Application.Orders.OrderPlacement>();
        services.AddScoped<Juvo.Application.Orders.CheckoutService>();
        services.AddScoped<Juvo.Application.Orders.CustomerOrdersService>();
        services.AddScoped<Juvo.Application.Orders.OrdersService>();
        services.AddScoped<Juvo.Application.Orders.OrderTimeoutService>();
        services.AddScoped<IOutboxMessageHandler, Juvo.Application.Orders.OrderPlacedHandler>();
        services.AddScoped<IOutboxMessageHandler, Juvo.Application.Orders.OrderStatusChangedHandler>();
        services.AddScoped<IOutboxMessageHandler, Juvo.Application.Orders.OrderBranchReassignedHandler>();
        services.AddScoped<IOutboxMessageHandler, Juvo.Application.Orders.OrderFlaggedHandler>();

        // Returns and exchanges (module L).
        services.AddScoped<Juvo.Application.Returns.ReturnPolicy>();
        services.AddScoped<Juvo.Application.Returns.ReturnOrderSync>();
        services.AddScoped<Juvo.Application.Returns.ReturnStock>();
        services.AddScoped<Juvo.Application.Returns.ReturnsService>();
        services.AddScoped<Juvo.Application.Returns.CustomerReturnsService>();
        services.AddScoped<IOutboxMessageHandler, Juvo.Application.Returns.ReturnStatusChangedHandler>();

        // Payments, refunds (module I), tax and invoices (module U), notifications (module K).
        services.AddScoped<Juvo.Application.Payments.IPaymentGatewayRegistry, Juvo.Application.Payments.PaymentGatewayRegistry>();
        services.AddScoped<Juvo.Application.Payments.PaymentSettingsService>();
        services.AddScoped<Juvo.Application.Payments.PaymentOutcomes>();
        services.AddScoped<Juvo.Application.Payments.CustomerPaymentsService>();
        services.AddScoped<Juvo.Application.Payments.PaymentWebhookService>();
        services.AddScoped<Juvo.Application.Payments.StaffPaymentsService>();
        services.AddScoped<Juvo.Application.Payments.RefundService>();
        services.AddScoped<Juvo.Application.Billing.TaxService>();
        services.AddScoped<Juvo.Application.Billing.InvoiceService>();
        services.AddScoped<Juvo.Application.Notifications.NotificationService>();
        services.AddScoped<Juvo.Application.Notifications.NotificationAdminService>();
        services.AddScoped<IOutboxMessageHandler, Juvo.Application.Notifications.NotificationDeliverHandler>();
        services.AddScoped<IOutboxMessageHandler, Juvo.Application.Orders.InvoiceOnConfirmedHandler>();

        // Couriers, shipments, COD settlement (module H) and real-time updates (module J).
        services.AddScoped<Juvo.Application.Couriers.ICourierProviderRegistry, Juvo.Application.Couriers.CourierProviderRegistry>();
        services.AddScoped<Juvo.Application.Couriers.CourierSelector>();
        services.AddScoped<Juvo.Application.Couriers.CourierAdminService>();
        services.AddScoped<Juvo.Application.Couriers.ShipmentViews>();
        services.AddScoped<Juvo.Application.Couriers.ShipmentCancellation>();
        services.AddScoped<Juvo.Application.Couriers.ShipmentSync>();
        services.AddScoped<Juvo.Application.Couriers.ShipmentBooking>();
        services.AddScoped<Juvo.Application.Couriers.ShipmentService>();
        services.AddScoped<Juvo.Application.Couriers.CourierWebhookService>();
        services.AddScoped<Juvo.Application.Couriers.CodSettlementService>();
        services.AddSingleton<Juvo.Application.Realtime.IRealtimePublisher, Juvo.Application.Realtime.NullRealtimePublisher>(); // the API replaces it with the SignalR publisher
        services.AddScoped<Juvo.Application.Realtime.RealtimeAccess>();
        services.AddScoped<Juvo.Application.Realtime.RealtimeTokens>();
        services.AddScoped<IOutboxMessageHandler, Juvo.Application.Realtime.RealtimeOrderStatusHandler>();
        services.AddScoped<IOutboxMessageHandler, Juvo.Application.Realtime.RealtimeOrderPlacedHandler>();
        services.AddScoped<IOutboxMessageHandler, Juvo.Application.Realtime.RealtimeShipmentHandler>();
        services.AddScoped<IOutboxMessageHandler, Juvo.Application.Realtime.RealtimeReturnHandler>();

        // Purchasing (W), expenses and the cash book (X), support (Y), reports (P) share these.
        services.AddScoped<Juvo.Application.Operations.OpsSettingsService>();
        services.AddScoped<Juvo.Application.Operations.NumberAllocator>();
        services.AddScoped<Juvo.Application.Operations.AttachmentStore>();
        services.AddScoped<Juvo.Application.Accounting.LedgerWriter>();
        services.AddScoped<Juvo.Application.Accounting.AccountsService>();
        services.AddScoped<Juvo.Application.Accounting.ExpenseCore>();
        services.AddScoped<Juvo.Application.Accounting.ExpensesService>();
        services.AddScoped<Juvo.Application.Accounting.RecurringExpenseService>();
        services.AddScoped<Juvo.Application.Purchasing.SuppliersService>();
        services.AddScoped<Juvo.Application.Purchasing.PurchasingService>();
        services.AddScoped<Juvo.Application.Support.TicketCore>();
        services.AddScoped<Juvo.Application.Support.TicketsService>();
        services.AddScoped<Juvo.Application.Support.MyTicketsService>();
        services.AddScoped<Juvo.Application.Support.CannedRepliesService>();
        services.AddScoped<Juvo.Application.Support.SupportReportService>();
        services.AddScoped<Juvo.Application.Support.TicketAutoCloseService>();
        services.AddScoped<IOutboxMessageHandler, Juvo.Application.Support.TicketCreatedHandler>();
        services.AddScoped<IOutboxMessageHandler, Juvo.Application.Support.TicketRepliedHandler>();
        services.AddScoped<IOutboxMessageHandler, Juvo.Application.Support.TicketResolvedHandler>();

        // Reports (module P) and the profit and loss (module X).
        services.AddScoped<Juvo.Application.Reports.ReportAccess>();
        services.AddScoped<Juvo.Application.Reports.ReportData>();
        services.AddScoped<Juvo.Application.Reports.FinanceBuilder>();
        services.AddScoped<Juvo.Application.Reports.PnlService>();
        services.AddScoped<Juvo.Application.Reports.FinanceReportService>();
        services.AddScoped<Juvo.Application.Reports.SalesReportService>();
        services.AddScoped<Juvo.Application.Reports.OrdersReportService>();
        services.AddScoped<Juvo.Application.Reports.InventoryReportService>();
        services.AddScoped<Juvo.Application.Reports.CourierReportService>();
        services.AddScoped<Juvo.Application.Reports.CustomersReportService>();
        services.AddScoped<Juvo.Application.Reports.DashboardService>();
        services.AddScoped<Juvo.Application.Reports.ReportFileWriter>();
        services.AddScoped<Juvo.Application.Reports.ReportExportService>();
        services.AddScoped<Juvo.Application.Reports.AccountantExportService>();
        services.AddScoped<Juvo.Application.Reports.DailySummaryService>();

        // Inventory (module V).
        services.AddScoped<InventoryAccess>();
        services.AddScoped<StockLedger>();
        services.AddScoped<StockService>();
        services.AddScoped<TransfersService>();
        services.AddScoped<CountsService>();

        // Promotions and the pricing engine (module Z), pre-orders and backorders (module AA).
        services.AddScoped<Juvo.Application.Pricing.IBasePriceSource, Juvo.Application.Pricing.CatalogBasePriceSource>();
        services.AddScoped<Juvo.Application.Pricing.IPricingEngine, Juvo.Application.Pricing.PricingEngine>();
        services.AddScoped<Juvo.Application.Pricing.PricedDelivery>();
        services.AddScoped<Juvo.Application.Promotions.PromotionsService>();
        services.AddScoped<Juvo.Application.Promotions.PromotionReportService>();
        services.AddScoped<Juvo.Application.Promotions.PromotionCardPricing>();
        services.AddScoped<Juvo.Application.Promotions.StorefrontPromotionsService>();
        services.AddScoped<Juvo.Application.Preorders.PreorderAllocator>();
        services.AddScoped<Juvo.Application.Preorders.PreorderService>();
        Juvo.Application.Loyalty.LoyaltyRegistration.AddLoyalty(services);
        // Marketing (module AC): segments, campaigns, automations, unsubscribe, back in stock.
        services.AddOptions<Juvo.Application.Marketing.MarketingOptions>();
        services.AddScoped<Juvo.Application.Marketing.MarketingSettingsService>();
        services.AddScoped<Juvo.Application.Marketing.SegmentQueryBuilder>();
        services.AddScoped<Juvo.Application.Marketing.SegmentsService>();
        services.AddScoped<Juvo.Application.Marketing.MarketingMessenger>();
        services.AddScoped<Juvo.Application.Marketing.AttributionService>();
        services.AddScoped<Juvo.Application.Marketing.CustomerFacts>();
        services.AddScoped<Juvo.Application.Marketing.CampaignDispatchService>();
        services.AddScoped<Juvo.Application.Marketing.CampaignsService>();
        services.AddScoped<Juvo.Application.Marketing.AutomationSeeder>();
        services.AddScoped<Juvo.Application.Marketing.AutomationEngine>();
        services.AddScoped<Juvo.Application.Marketing.AutomationsService>();
        services.AddScoped<Juvo.Application.Marketing.UnsubscribeService>();
        services.AddScoped<Juvo.Application.Marketing.BackInStockService>();

        // No branch scope unless the host provides one (the API registers the per-request scope).
        services.TryAddScoped<IBranchContext>(_ => UnscopedBranchContext.Instance);

        return services;
    }
}
