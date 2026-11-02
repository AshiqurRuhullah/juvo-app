using System.Security.Cryptography;
using System.Text;

namespace Juvo.Shared.Security;

/// <summary>One assignable permission: <c>module.resource.action</c>, for example <c>orders.order.confirm</c>.</summary>
public sealed record PermissionDefinition(string Code, string Module, string Resource, string Action, string Description);

/// <summary>
/// The complete, code-defined permission catalog (every module and admin action of the SRS, modules A to AF).
/// It is seeded to the database on start-up (idempotent) and versioned by a hash of its codes.
/// Add new permissions here; never rename or reuse a code.
/// </summary>
public static class PermissionCatalog
{
    private static readonly Lazy<IReadOnlyList<PermissionDefinition>> LazyAll = new(Build);
    private static readonly Lazy<string> LazyVersion = new(() =>
    {
        var codes = string.Join('\n', LazyAll.Value.Select(p => p.Code).Order(StringComparer.Ordinal));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(codes)))[..12].ToLowerInvariant();
    });

    public static IReadOnlyList<PermissionDefinition> All => LazyAll.Value;

    /// <summary>Changes whenever a permission is added or removed. Returned by the permission catalog endpoint.</summary>
    public static string Version => LazyVersion.Value;

    public static bool Exists(string code) => ByCode.Value.ContainsKey(code);

    private static readonly Lazy<Dictionary<string, PermissionDefinition>> ByCode =
        new(() => LazyAll.Value.ToDictionary(p => p.Code, StringComparer.Ordinal));

    private static List<PermissionDefinition> Build()
    {
        var list = new List<PermissionDefinition>();

        void Add(string module, string resource, string actions)
        {
            foreach (var action in actions.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                list.Add(new PermissionDefinition(
                    $"{module}.{resource}.{action}", module, resource, action,
                    $"{Humanize(action)} {Humanize(resource).ToLowerInvariant()}"));
            }
        }

        // ---- Identity, users, roles, branches (module O, section 4A) ----
        Add("ums", "user", "view create edit activate deactivate reset-password force-logout manage-two-factor");
        Add("ums", "role", "view create edit delete clone assign-permissions");
        Add("ums", "permission", "view");
        Add("ums", "branch", "view create edit deactivate set-default assign-user switch-all");
        Add("ums", "session", "view revoke");
        Add("ums", "sign-in", "view");
        Add("ums", "audit", "view");

        // ---- Orders, social desk, returns (modules D, J, L, S) ----
        Add("orders", "order", "view create edit confirm pack cancel reassign-branch export note bulk-action print");
        Add("orders", "source", "view");
        Add("returns", "return", "view approve reject receive refund exchange");

        // ---- Customers, fraud, support (modules E, F, T, Y) ----
        Add("customers", "customer", "view edit block export view-pii");
        Add("fraud", "flag", "view");
        Add("fraud", "review", "decide");
        Add("fraud", "blacklist", "view create delete");
        Add("fraud", "rule", "view edit");
        Add("support", "ticket", "view create assign reply close");
        Add("support", "canned-reply", "view edit");
        Add("support", "report", "view");

        // ---- Catalog and inventory (modules A, B, V, AA) ----
        Add("catalog", "product", "view create edit publish archive duplicate import export view-cost");
        Add("catalog", "category", "view create edit delete");
        Add("catalog", "brand", "view create edit delete");
        Add("catalog", "size-chart", "view edit");
        Add("catalog", "media", "upload delete");
        Add("catalog", "review", "view moderate");
        Add("inventory", "stock", "view adjust");
        Add("inventory", "transfer", "view request approve receive cancel");
        Add("inventory", "count", "view create approve");
        Add("inventory", "preorder", "view manage");
        Add("inventory", "reorder", "view edit");

        // ---- Purchasing, accounting (modules W, X) ----
        Add("purchasing", "supplier", "view create edit");
        Add("purchasing", "po", "view create edit send receive cancel");
        Add("purchasing", "receipt", "view create");
        Add("purchasing", "payable", "view pay");
        Add("accounting", "expense", "view create edit delete");
        Add("accounting", "cashbook", "view edit");
        Add("accounting", "pnl", "view");
        Add("accounting", "export", "run");

        // ---- Delivery, couriers, payments, tax (modules G, H, I, U) ----
        Add("delivery", "zone", "view create edit delete");
        Add("delivery", "rate", "view edit");
        Add("delivery", "country", "view enable");
        Add("delivery", "restricted-item", "view edit");
        Add("couriers", "courier", "view edit manage-credentials");
        Add("couriers", "shipment", "view book cancel print-label bulk-book");
        Add("couriers", "cod", "view settle");
        Add("couriers", "webhook", "view");
        Add("payments", "payment", "view verify");
        Add("payments", "refund", "view create approve");
        Add("payments", "gateway", "view edit");
        Add("payments", "ledger", "view reconcile");
        Add("tax", "rule", "view edit");
        Add("tax", "invoice", "view issue download");
        Add("tax", "credit-note", "create");
        Add("tax", "report", "view");

        // ---- Marketing and growth (modules Z, AB, AC, AD, AE, K) ----
        Add("promotions", "promotion", "view create edit delete");
        Add("promotions", "coupon", "view create edit delete");
        Add("loyalty", "points", "view adjust");
        Add("loyalty", "gift-card", "view create");
        Add("loyalty", "referral", "view");
        Add("marketing", "segment", "view edit");
        Add("marketing", "campaign", "view create send");
        Add("marketing", "automation", "view edit");
        Add("partners", "reseller", "view edit");
        Add("partners", "affiliate", "view edit");
        Add("partners", "commission", "view pay");
        Add("pos", "sale", "create view refund");
        Add("pos", "register", "open close view");
        Add("notifications", "template", "view edit");
        Add("notifications", "log", "view retry");
        Add("notifications", "channel", "edit");

        // ---- Reports, CMS, settings, platform (modules M, P, Q, R, AF) ----
        Add("reports", "dashboard", "view");
        Add("reports", "sales", "view export");
        Add("reports", "orders", "view export");
        Add("reports", "inventory", "view export");
        Add("reports", "finance", "view export");
        Add("reports", "courier", "view export");
        Add("reports", "customers", "view export");
        Add("cms", "page", "view edit publish");
        Add("cms", "contact", "view reply");
        Add("cms", "seo", "edit");
        Add("cms", "social", "edit");
        Add("settings", "system", "view edit");
        Add("settings", "integration", "view edit");
        Add("settings", "feature", "edit");
        Add("platform", "job", "view retry");
        Add("platform", "apikey", "view create revoke");
        Add("platform", "webhook", "view edit");
        Add("platform", "backup", "run restore");
        Add("platform", "health", "view");
        Add("privacy", "data", "view-consent export anonymise");

        return list;
    }

    private static string Humanize(string kebab)
    {
        var text = kebab.Replace('-', ' ');
        return char.ToUpperInvariant(text[0]) + text[1..];
    }
}
