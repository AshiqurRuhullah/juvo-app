using System.Text.RegularExpressions;
using Juvo.Shared.Security;

namespace Juvo.Domain.Tests.Shared;

public partial class PermissionCatalogTests
{
    [GeneratedRegex(@"^[a-z]+\.[a-z]+(-[a-z]+)*\.[a-z]+(-[a-z]+)*$")]
    private static partial Regex CodeFormat();

    [Fact]
    public void Codes_are_unique_and_follow_module_resource_action()
    {
        var codes = PermissionCatalog.All.Select(p => p.Code).ToList();

        Assert.Equal(codes.Count, codes.Distinct(StringComparer.Ordinal).Count());
        Assert.All(codes, code => Assert.Matches(CodeFormat(), code));
        Assert.All(PermissionCatalog.All, p => Assert.Equal($"{p.Module}.{p.Resource}.{p.Action}", p.Code));
    }

    [Fact]
    public void Catalog_covers_every_module_of_the_srs()
    {
        var modules = PermissionCatalog.All.Select(p => p.Module).ToHashSet();

        foreach (var expected in new[]
                 {
                     "orders", "returns", "customers", "catalog", "inventory", "purchasing", "delivery", "couriers",
                     "payments", "tax", "notifications", "fraud", "support", "promotions", "loyalty", "marketing",
                     "partners", "pos", "accounting", "reports", "cms", "settings", "platform", "ums",
                 })
        {
            Assert.Contains(expected, modules);
        }
    }

    [Fact]
    public void Required_permissions_from_the_master_prompt_exist()
    {
        foreach (var code in new[]
                 {
                     "orders.order.view", "orders.order.confirm", "orders.order.cancel", "catalog.product.create",
                     "catalog.product.publish", "inventory.stock.adjust", "payments.refund.approve", "couriers.shipment.book",
                     "reports.finance.view", "ums.user.create", "settings.system.edit", "ums.branch.assign-user",
                     "ums.branch.switch-all", "inventory.transfer.request", "inventory.transfer.approve",
                     "inventory.transfer.receive", "orders.order.reassign-branch", "ums.role.assign-permissions",
                 })
        {
            Assert.True(PermissionCatalog.Exists(code), $"{code} missing from the catalog");
        }
    }

    [Fact]
    public void Version_is_stable_and_short()
    {
        Assert.Equal(PermissionCatalog.Version, PermissionCatalog.Version);
        Assert.Equal(12, PermissionCatalog.Version.Length);
    }

    [Fact]
    public void Constants_used_in_code_all_exist_in_the_catalog()
    {
        var constants = typeof(Permissions.Ums).GetFields()
            .Where(f => f.IsLiteral)
            .Select(f => (string)f.GetRawConstantValue()!);

        Assert.All(constants, code => Assert.True(PermissionCatalog.Exists(code), $"{code} is not in the catalog"));
    }

    [Fact]
    public void Every_default_role_pattern_matches_something_and_names_are_unique()
    {
        Assert.Equal(7, DefaultRoles.All.Count);
        Assert.Equal(DefaultRoles.All.Count, DefaultRoles.All.Select(r => r.Name).Distinct().Count());

        foreach (var role in DefaultRoles.All.Where(r => !r.IsSuperAdmin))
        {
            Assert.All(role.Allow, pattern =>
                Assert.True(PermissionCatalog.All.Any(p => PermissionPattern.Matches(pattern, p.Code)),
                    $"{role.Name}: pattern '{pattern}' matches no permission"));
            Assert.NotEmpty(PermissionPattern.Expand(role));
        }
    }

    [Fact]
    public void Default_roles_follow_the_srs_roles_matrix()
    {
        IReadOnlyList<string> Of(string name) => PermissionPattern.Expand(DefaultRoles.All.Single(r => r.Name == name));

        // Settings and roles belong to the Super Admin only.
        foreach (var role in new[] { "Manager", "Order Manager", "Inventory Manager", "Content Editor", "Support", "Accountant" })
        {
            var codes = Of(role);
            Assert.DoesNotContain(codes, c => c.StartsWith("settings.", StringComparison.Ordinal));
            Assert.DoesNotContain(codes, c => c.StartsWith("ums.role.", StringComparison.Ordinal));
        }

        // Cost price and profit are hidden from roles without the permission (FR-O04).
        Assert.DoesNotContain("catalog.product.view-cost", Of("Manager"));
        Assert.Contains("catalog.product.view-cost", Of("Accountant"));
        Assert.Contains("reports.finance.view", Of("Accountant"));
        Assert.DoesNotContain("reports.finance.view", Of("Order Manager"));

        // Order manager runs orders; inventory manager does not confirm orders.
        Assert.Contains("orders.order.confirm", Of("Order Manager"));
        Assert.DoesNotContain("orders.order.confirm", Of("Inventory Manager"));
        Assert.Contains("inventory.stock.adjust", Of("Inventory Manager"));

        // Content editor edits content, not orders or money.
        Assert.Contains("cms.page.publish", Of("Content Editor"));
        Assert.DoesNotContain(Of("Content Editor"), c => c.StartsWith("orders.", StringComparison.Ordinal));
        Assert.DoesNotContain(Of("Content Editor"), c => c.StartsWith("payments.", StringComparison.Ordinal));

        // Accountant views payments but cannot approve refunds.
        Assert.Contains("payments.payment.view", Of("Accountant"));
        Assert.DoesNotContain("payments.refund.approve", Of("Accountant"));
        Assert.Contains("payments.refund.approve", Of("Manager"));
    }

    [Theory]
    [InlineData("orders.*", "orders.order.view", true)]
    [InlineData("orders.order.*", "orders.order.view", true)]
    [InlineData("orders.order.*", "orders.source.view", false)]
    [InlineData("orders.order.view", "orders.order.view", true)]
    [InlineData("orders.order.view", "orders.order.viewx", false)]
    [InlineData("order.*", "orders.order.view", false)]
    public void Pattern_matching(string pattern, string code, bool expected) =>
        Assert.Equal(expected, PermissionPattern.Matches(pattern, code));
}
