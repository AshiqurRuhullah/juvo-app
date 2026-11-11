namespace Juvo.Shared.Security;

/// <summary>A built-in role. <see cref="Allow"/> patterns are expanded against the catalog when the role is first created.</summary>
public sealed record DefaultRoleDefinition(string Name, string Description, bool IsSuperAdmin, IReadOnlyList<string> Allow, IReadOnlyList<string> Deny);

/// <summary>
/// Default roles from the SRS roles matrix (module O). Pattern forms: <c>module.*</c>, <c>module.resource.*</c> or an exact code.
/// Super Admin is special: it holds every permission, including ones added later, without stored rows.
/// </summary>
public static class DefaultRoles
{
    public const string SuperAdmin = "Super Admin";
    public const string Manager = "Manager";
    public const string OrderManager = "Order Manager";
    public const string InventoryManager = "Inventory Manager";
    public const string ContentEditor = "Content Editor";
    public const string Support = "Support";
    public const string Accountant = "Accountant";

    public static IReadOnlyList<DefaultRoleDefinition> All { get; } =
    [
        new(SuperAdmin, "Owner. Full control, including roles, settings, reports and money data.", true, [], []),

        new(Manager, "Runs daily operations. Everything except settings, roles and platform tools.", false,
        [
            "orders.*", "returns.*", "customers.*", "catalog.*", "inventory.*", "purchasing.*", "delivery.*", "couriers.*",
            "payments.payment.*", "payments.refund.*", "payments.ledger.view", "tax.*", "notifications.*", "fraud.*",
            "support.*", "promotions.*", "loyalty.*", "marketing.*", "partners.*", "pos.*", "accounting.*", "reports.*",
            "cms.*", "platform.job.view", "ums.user.view", "ums.branch.view", "ums.audit.view",
        ],
        ["catalog.product.view-cost", "reports.finance.*", "accounting.pnl.view", "accounting.export.run"]),

        new(OrderManager, "Processes orders and shipments.", false,
        [
            "orders.*", "returns.*", "customers.customer.view", "delivery.zone.view", "couriers.shipment.*",
            "couriers.courier.view", "couriers.cod.view", "payments.payment.view", "reports.orders.view",
            "fraud.flag.view", "notifications.log.view", "catalog.product.view", "inventory.stock.view", "support.ticket.view",
        ],
        ["orders.order.reassign-branch", "returns.return.refund"]),

        new(InventoryManager, "Maintains products, stock, transfers and purchasing.", false,
        [
            "catalog.*", "inventory.*", "purchasing.*", "orders.order.view", "returns.return.view", "returns.return.receive", "returns.return.inspect", "reports.inventory.*",
        ],
        ["catalog.product.view-cost", "purchasing.payable.pay"]),

        new(ContentEditor, "Edits website content and product content.", false,
        [
            "cms.*", "catalog.product.view", "catalog.product.edit", "catalog.category.view", "catalog.media.upload",
            "catalog.review.*", "catalog.size-chart.*",
        ],
        []),

        new(Support, "Answers customers and handles tickets.", false,
        [
            "orders.order.view", "orders.order.note", "customers.customer.view", "support.*", "returns.return.view", "returns.return.approve",
            "couriers.shipment.view", "delivery.zone.view", "notifications.log.view",
        ],
        []),

        new(Accountant, "Views money data and runs finance reports.", false,
        [
            "payments.payment.view", "payments.refund.view", "payments.ledger.*", "tax.*", "accounting.*", "reports.finance.*",
            "orders.order.view", "returns.return.view", "returns.return.refund", "couriers.cod.*", "customers.customer.view", "purchasing.payable.*", "catalog.product.view",
            "catalog.product.view-cost",
        ],
        []),
    ];
}

/// <summary>Matches permission codes against <c>module.*</c>, <c>module.resource.*</c> and exact patterns.</summary>
public static class PermissionPattern
{
    public static bool Matches(string pattern, string code) =>
        pattern.EndsWith(".*", StringComparison.Ordinal)
            ? code.StartsWith(pattern[..^1], StringComparison.Ordinal)
            : string.Equals(pattern, code, StringComparison.Ordinal);

    /// <summary>Codes granted by a role definition: allowed by any Allow pattern and not removed by a Deny pattern.</summary>
    public static IReadOnlyList<string> Expand(DefaultRoleDefinition role) =>
        PermissionCatalog.All
            .Select(p => p.Code)
            .Where(code => role.Allow.Any(a => Matches(a, code)) && !role.Deny.Any(d => Matches(d, code)))
            .ToList();
}
