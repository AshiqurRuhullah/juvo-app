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

        // Inventory (module V).
        services.AddScoped<InventoryAccess>();
        services.AddScoped<StockLedger>();
        services.AddScoped<StockService>();
        services.AddScoped<TransfersService>();
        services.AddScoped<CountsService>();

        // No branch scope unless the host provides one (the API registers the per-request scope).
        services.TryAddScoped<IBranchContext>(_ => UnscopedBranchContext.Instance);

        return services;
    }
}
