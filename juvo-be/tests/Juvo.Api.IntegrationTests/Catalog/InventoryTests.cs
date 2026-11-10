using System.Net;
using System.Text.Json;
using Juvo.Api.IntegrationTests.Support;
using Juvo.Domain.Inventory;
using Juvo.Domain.Platform;
using Juvo.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Juvo.Api.IntegrationTests.Catalog;

/// <summary>Branch-wise stock, adjustments, the ledger, transfers and stocktakes through the real API (module V, SRS 4A.3).</summary>
public class InventoryTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly TestApi _api = new(factory);

    private static string Code() => "I" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();

    private static string Email(string prefix) => $"{prefix}-{Guid.NewGuid():N}"[..(prefix.Length + 9)] + "@juvo.test";

    private static readonly string[] StaffPermissions =
    [
        "inventory.stock.view", "inventory.stock.adjust", "inventory.reorder.view", "inventory.reorder.edit", "inventory.transfer.view",
        "inventory.transfer.request", "inventory.transfer.approve", "inventory.transfer.receive", "inventory.transfer.cancel",
        "inventory.count.view", "inventory.count.create", "inventory.count.approve", "catalog.product.view",
    ];

    private sealed record World(Tokens Admin, Guid A, Guid B, Tokens Alice, Tokens Bob, Guid AliceId, Guid BobId, Guid VariantId, Guid ProductId, string Sku);

    /// <summary>Two new branches, a stock manager in each (own branch only), and one product with one variant.</summary>
    private async Task<World> StartAsync(params string[] permissions)
    {
        var admin = await _api.LoginAdminAsync();
        var a = await _api.CreateBranchAsync(admin.Access, Code(), "Stock A");
        var b = await _api.CreateBranchAsync(admin.Access, Code(), "Stock B");
        var role = await _api.CreateRoleAsync(admin.Access, "Stock-" + Guid.NewGuid().ToString("N")[..8], permissions.Length > 0 ? permissions : StaffPermissions);
        var alice = await _api.CreateUserAsync(admin.Access, Email("alice"), "Alice A", false, (a, role));
        var bob = await _api.CreateUserAsync(admin.Access, Email("bob"), "Bob B", false, (b, role));
        var (productId, variantId, sku) = await NewVariantAsync(admin.Access);
        return new World(admin, a, b, await _api.LoginAsync(alice.Email, alice.Password), await _api.LoginAsync(bob.Email, bob.Password),
            alice.Id, bob.Id, variantId, productId, sku);
    }

    private async Task<(Guid ProductId, Guid VariantId, string Sku)> NewVariantAsync(string adminToken, string? namePrefix = null)
    {
        var category = await TestApi.ExpectAsync(await _api.PostAsync("/api/v1/catalog/categories",
            new { nameEn = $"Inv-{Guid.NewGuid():N}"[..14], sortOrder = 0 }, adminToken), HttpStatusCode.Created);
        var product = await TestApi.ExpectAsync(await _api.PostAsync("/api/v1/catalog/products", new
        {
            nameEn = $"{namePrefix ?? "Shirt"}-{Guid.NewGuid():N}"[..16], categoryId = category.GetProperty("id").GetGuid(), regularPrice = 900,
            targetGroup = "Unisex", isNew = false, isFeatured = false, isBestSeller = false,
        }, adminToken), HttpStatusCode.Created);
        var productId = product.GetProperty("id").GetGuid();
        var sku = "SKU-" + Guid.NewGuid().ToString("N")[..10].ToUpperInvariant();
        var variant = await TestApi.ExpectAsync(await _api.PostAsync($"/api/v1/catalog/products/{productId}/variants",
            new { sku, colorName = "Red", size = "M" }, adminToken), HttpStatusCode.Created);
        return (productId, variant.GetProperty("id").GetGuid(), sku);
    }

    private Task<HttpResponseMessage> AdjustAsync(string token, Guid branch, Guid variant, int change, string reason = "OpeningStock", string? note = null) =>
        _api.PostAsync("/api/v1/inventory/adjustments", new { branchId = branch, reason, note, lines = new[] { new { variantId = variant, quantityChange = change } } }, token);

    private async Task StockAsync(string token, Guid branch, Guid variant, int quantity) =>
        await TestApi.ExpectAsync(await AdjustAsync(token, branch, variant, quantity), HttpStatusCode.OK);

    private async Task<JsonElement> RowAsync(string token, Guid branch, Guid variant)
    {
        var list = await TestApi.ExpectAsync(await _api.GetAsync($"/api/v1/inventory/stock?branchId={branch}&pageSize=100", token), HttpStatusCode.OK);
        return list.GetProperty("items").EnumerateArray().Single(i => i.GetProperty("variantId").GetGuid() == variant);
    }

    private async Task<int> OnHandAsync(string token, Guid branch, Guid variant) => (await RowAsync(token, branch, variant)).GetProperty("onHand").GetInt32();

    private async Task<int> AccessDeniedCountAsync(Guid userId)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<JuvoDbContext>();
        return await db.AuditLogs.CountAsync(a => a.UserId == userId && a.Action == AuditAction.AccessDenied, Ct);
    }

    private async Task<JsonElement> MovementsAsync(string token, Guid branch, Guid variant) =>
        (await TestApi.ExpectAsync(await _api.GetAsync($"/api/v1/inventory/movements?branchId={branch}&variantId={variant}", token), HttpStatusCode.OK)).GetProperty("items");

    // ---- stock and the ledger ---------------------------------------------------------------------------

    [Fact]
    public async Task Opening_stock_creates_the_level_and_one_ledger_line()
    {
        var w = await StartAsync();

        var rows = await TestApi.ExpectAsync(await AdjustAsync(w.Alice.Access, w.A, w.VariantId, 10, note: "First count"), HttpStatusCode.OK);

        var row = Assert.Single(rows.EnumerateArray());
        Assert.Equal((10, 0, 10), (row.GetProperty("onHand").GetInt32(), row.GetProperty("reserved").GetInt32(), row.GetProperty("available").GetInt32()));
        Assert.Equal(w.Sku, row.GetProperty("sku").GetString());

        var movement = Assert.Single((await MovementsAsync(w.Alice.Access, w.A, w.VariantId)).EnumerateArray());
        Assert.Equal(10, movement.GetProperty("quantityChange").GetInt32());
        Assert.Equal(10, movement.GetProperty("onHandAfter").GetInt32());
        Assert.Equal("OpeningStock", movement.GetProperty("reason").GetString());
        Assert.Equal("First count", movement.GetProperty("note").GetString());
        Assert.Equal("Alice A", movement.GetProperty("createdByName").GetString());
    }

    [Fact]
    public async Task Removing_more_than_is_available_is_refused_and_a_failing_line_changes_nothing()
    {
        var w = await StartAsync();
        await StockAsync(w.Alice.Access, w.A, w.VariantId, 3);
        var (_, second, _) = await NewVariantAsync(w.Admin.Access);
        await StockAsync(w.Alice.Access, w.A, second, 5);

        var response = await _api.PostAsync("/api/v1/inventory/adjustments", new
        {
            branchId = w.A, reason = "Loss",
            lines = new object[] { new { variantId = second, quantityChange = -2 }, new { variantId = w.VariantId, quantityChange = -4 } },
        }, w.Alice.Access);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("INVENTORY.INSUFFICIENT_STOCK", await TestApi.ErrorCodeAsync(response));
        Assert.Equal(3, await OnHandAsync(w.Alice.Access, w.A, w.VariantId));
        Assert.Equal(5, await OnHandAsync(w.Alice.Access, w.A, second)); // the first line was not applied either
    }

    [Fact]
    public async Task Reasons_decide_the_sign_and_system_reasons_cannot_be_used_by_hand()
    {
        var w = await StartAsync();
        await StockAsync(w.Alice.Access, w.A, w.VariantId, 10);

        Assert.Equal(HttpStatusCode.BadRequest, (await AdjustAsync(w.Alice.Access, w.A, w.VariantId, 2, "Damage")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await AdjustAsync(w.Alice.Access, w.A, w.VariantId, -2, "Found")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await AdjustAsync(w.Alice.Access, w.A, w.VariantId, 0, "Correction")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await AdjustAsync(w.Alice.Access, w.A, w.VariantId, 5, "TransferIn")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await AdjustAsync(w.Alice.Access, w.A, w.VariantId, 5, "StockCount")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await AdjustAsync(w.Alice.Access, w.A, w.VariantId, -2, "Damage")).StatusCode);
        Assert.Equal(8, await OnHandAsync(w.Alice.Access, w.A, w.VariantId));
    }

    [Fact]
    public async Task A_global_user_without_an_active_branch_must_name_one_and_an_unknown_variant_is_404()
    {
        var w = await StartAsync();

        var noBranch = await _api.PostAsync("/api/v1/inventory/adjustments",
            new { reason = "OpeningStock", lines = new[] { new { variantId = w.VariantId, quantityChange = 1 } } }, w.Admin.Access);
        Assert.Equal(HttpStatusCode.BadRequest, noBranch.StatusCode);

        var unknown = await AdjustAsync(w.Admin.Access, w.A, Guid.NewGuid(), 1);
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        Assert.Equal("INVENTORY.VARIANT_NOT_FOUND", await TestApi.ErrorCodeAsync(unknown));

        // The active branch header is used when no branch is in the body.
        var viaHeader = await _api.PostAsync("/api/v1/inventory/adjustments",
            new { reason = "OpeningStock", lines = new[] { new { variantId = w.VariantId, quantityChange = 4 } } }, w.Admin.Access, w.A);
        await TestApi.ExpectAsync(viaHeader, HttpStatusCode.OK);
        Assert.Equal(4, await OnHandAsync(w.Admin.Access, w.A, w.VariantId));
    }

    [Fact]
    public async Task Low_stock_threshold_flags_the_row_and_the_low_only_filter_finds_it()
    {
        var w = await StartAsync();
        await StockAsync(w.Alice.Access, w.A, w.VariantId, 5);

        var set = await TestApi.ExpectAsync(await _api.PutAsync("/api/v1/inventory/reorder-levels",
            new { branchId = w.A, variantId = w.VariantId, reorderLevel = 5 }, w.Alice.Access), HttpStatusCode.OK);
        Assert.True(set.GetProperty("isLow").GetBoolean());

        var low = await TestApi.ExpectAsync(await _api.GetAsync($"/api/v1/inventory/stock?branchId={w.A}&lowOnly=true", w.Alice.Access), HttpStatusCode.OK);
        Assert.Single(low.GetProperty("items").EnumerateArray());

        await StockAsync(w.Alice.Access, w.A, w.VariantId, 10);
        var after = await TestApi.ExpectAsync(await _api.GetAsync($"/api/v1/inventory/stock?branchId={w.A}&lowOnly=true", w.Alice.Access), HttpStatusCode.OK);
        Assert.Empty(after.GetProperty("items").EnumerateArray());

        var negative = await _api.PutAsync("/api/v1/inventory/reorder-levels", new { branchId = w.A, variantId = w.VariantId, reorderLevel = -1 }, w.Alice.Access);
        Assert.Equal(HttpStatusCode.BadRequest, negative.StatusCode);
    }

    [Fact]
    public async Task The_product_grid_lists_every_variant_against_every_branch_the_user_may_view()
    {
        var w = await StartAsync();
        await StockAsync(w.Admin.Access, w.A, w.VariantId, 6);
        await StockAsync(w.Admin.Access, w.B, w.VariantId, 2);

        var grid = await TestApi.ExpectAsync(await _api.GetAsync($"/api/v1/inventory/stock/products/{w.ProductId}", w.Admin.Access), HttpStatusCode.OK);
        var variant = Assert.Single(grid.GetProperty("variants").EnumerateArray());
        var branches = variant.GetProperty("branches").EnumerateArray().ToDictionary(b => b.GetProperty("branchId").GetGuid());
        Assert.Equal(6, branches[w.A].GetProperty("onHand").GetInt32());
        Assert.Equal(2, branches[w.B].GetProperty("onHand").GetInt32());
        Assert.True(variant.GetProperty("onHand").GetInt32() >= 8);

        // Alice sees her branch only, never B's quantities.
        var aliceGrid = await TestApi.ExpectAsync(await _api.GetAsync($"/api/v1/inventory/stock/products/{w.ProductId}", w.Alice.Access), HttpStatusCode.OK);
        var aliceBranches = Assert.Single(aliceGrid.GetProperty("variants").EnumerateArray()).GetProperty("branches").EnumerateArray().ToList();
        Assert.Equal(w.A, Assert.Single(aliceBranches).GetProperty("branchId").GetGuid());
        Assert.Equal(6, Assert.Single(aliceBranches).GetProperty("onHand").GetInt32());
    }

    // ---- isolation and permissions ------------------------------------------------------------------------

    [Fact]
    public async Task Branch_staff_cannot_read_or_change_another_branchs_stock_and_the_attempt_is_audit_logged()
    {
        var w = await StartAsync();
        await StockAsync(w.Alice.Access, w.A, w.VariantId, 10);

        var bobList = await TestApi.ExpectAsync(await _api.GetAsync("/api/v1/inventory/stock?pageSize=100", w.Bob.Access), HttpStatusCode.OK);
        Assert.DoesNotContain(bobList.GetProperty("items").EnumerateArray(), i => i.GetProperty("branchId").GetGuid() == w.A);
        var bobLedger = await TestApi.ExpectAsync(await _api.GetAsync($"/api/v1/inventory/movements?branchId={w.A}", w.Bob.Access), HttpStatusCode.OK);
        Assert.Empty(bobLedger.GetProperty("items").EnumerateArray());

        var bobId = w.BobId;
        var before = await AccessDeniedCountAsync(bobId);
        var write = await AdjustAsync(w.Bob.Access, w.A, w.VariantId, -5, "Loss");
        Assert.Equal(HttpStatusCode.NotFound, write.StatusCode);
        Assert.True(await AccessDeniedCountAsync(bobId) > before);
        Assert.Equal(10, await OnHandAsync(w.Alice.Access, w.A, w.VariantId));
    }

    [Fact]
    public async Task A_user_without_the_adjust_permission_in_their_branch_gets_403()
    {
        var w = await StartAsync("inventory.stock.view", "catalog.product.view");
        await StockAsync(w.Admin.Access, w.A, w.VariantId, 5);

        var response = await AdjustAsync(w.Alice.Access, w.A, w.VariantId, 1);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(5, await OnHandAsync(w.Alice.Access, w.A, w.VariantId));
    }

    // ---- transfers ----------------------------------------------------------------------------------------

    private async Task<Guid> RequestTransferAsync(string token, Guid from, Guid to, Guid variant, int quantity)
    {
        var response = await _api.PostAsync("/api/v1/inventory/transfers",
            new { fromBranchId = from, toBranchId = to, note = "Restock", lines = new[] { new { variantId = variant, quantity } } }, token);
        return (await TestApi.ExpectAsync(response, HttpStatusCode.Created)).GetProperty("id").GetGuid();
    }

    private Task<HttpResponseMessage> StepAsync(string token, Guid id, string step) => _api.PostAsync($"/api/v1/inventory/transfers/{id}/{step}", new { reason = "test" }, token);

    private async Task<JsonElement> TransferAsync(string token, Guid id) =>
        await TestApi.ExpectAsync(await _api.GetAsync($"/api/v1/inventory/transfers/{id}", token), HttpStatusCode.OK);

    [Fact]
    public async Task A_transfer_moves_stock_from_the_source_to_the_destination_with_a_movement_on_each_side()
    {
        var w = await StartAsync();
        await StockAsync(w.Alice.Access, w.A, w.VariantId, 10);

        // The destination asks; the source approves and ships; the destination receives.
        var id = await RequestTransferAsync(w.Bob.Access, w.A, w.B, w.VariantId, 4);
        var requested = await TransferAsync(w.Bob.Access, id);
        Assert.Equal("Requested", requested.GetProperty("status").GetString());
        Assert.StartsWith("TR-", requested.GetProperty("number").GetString());
        Assert.Equal(["cancel"], requested.GetProperty("allowedActions").EnumerateArray().Select(a => a.GetString()).ToList()); // Bob cannot approve in A

        Assert.Equal(HttpStatusCode.Forbidden, (await StepAsync(w.Bob.Access, id, "approve")).StatusCode);
        await TestApi.ExpectAsync(await StepAsync(w.Alice.Access, id, "approve"), HttpStatusCode.OK);
        Assert.Equal(10, await OnHandAsync(w.Alice.Access, w.A, w.VariantId)); // nothing moves before shipping

        var shipped = await TestApi.ExpectAsync(await StepAsync(w.Alice.Access, id, "ship"), HttpStatusCode.OK);
        Assert.Equal("InTransit", shipped.GetProperty("status").GetString());
        Assert.Equal(6, await OnHandAsync(w.Alice.Access, w.A, w.VariantId));

        Assert.Equal(HttpStatusCode.Forbidden, (await StepAsync(w.Alice.Access, id, "receive")).StatusCode); // only the destination receives
        var received = await TestApi.ExpectAsync(await StepAsync(w.Bob.Access, id, "receive"), HttpStatusCode.OK);
        Assert.Equal("Received", received.GetProperty("status").GetString());
        Assert.Equal(4, await OnHandAsync(w.Bob.Access, w.B, w.VariantId));

        var out1 = Assert.Single((await MovementsAsync(w.Alice.Access, w.A, w.VariantId)).EnumerateArray(), m => m.GetProperty("reason").GetString() == "TransferOut");
        Assert.Equal(-4, out1.GetProperty("quantityChange").GetInt32());
        Assert.Equal(6, out1.GetProperty("onHandAfter").GetInt32());
        var in1 = Assert.Single((await MovementsAsync(w.Bob.Access, w.B, w.VariantId)).EnumerateArray());
        Assert.Equal(("TransferIn", 4, "Transfer"), (in1.GetProperty("reason").GetString(), in1.GetProperty("quantityChange").GetInt32(), in1.GetProperty("referenceType").GetString()));
        Assert.Equal(id, in1.GetProperty("referenceId").GetGuid());

        // Both sides see the transfer; each only sees their own stock.
        Assert.Equal(HttpStatusCode.OK, (await _api.GetAsync($"/api/v1/inventory/transfers/{id}", w.Alice.Access)).StatusCode);
        Assert.Empty((await TestApi.ExpectAsync(await _api.GetAsync($"/api/v1/inventory/stock?branchId={w.B}", w.Alice.Access), HttpStatusCode.OK)).GetProperty("items").EnumerateArray());
    }

    [Fact]
    public async Task Steps_cannot_be_skipped_or_repeated_and_a_third_branch_cannot_see_the_transfer()
    {
        var w = await StartAsync();
        await StockAsync(w.Alice.Access, w.A, w.VariantId, 10);
        var id = await RequestTransferAsync(w.Alice.Access, w.A, w.B, w.VariantId, 2);

        Assert.Equal("INVENTORY.TRANSFER_STATE", await TestApi.ErrorCodeAsync(await StepAsync(w.Alice.Access, id, "ship")));
        Assert.Equal("INVENTORY.TRANSFER_STATE", await TestApi.ErrorCodeAsync(await StepAsync(w.Bob.Access, id, "receive")));
        await TestApi.ExpectAsync(await StepAsync(w.Alice.Access, id, "approve"), HttpStatusCode.OK);
        Assert.Equal("INVENTORY.TRANSFER_STATE", await TestApi.ErrorCodeAsync(await StepAsync(w.Alice.Access, id, "approve")));
        Assert.Equal("INVENTORY.TRANSFER_STATE", await TestApi.ErrorCodeAsync(await StepAsync(w.Alice.Access, id, "reject")));

        var c = await _api.CreateBranchAsync(w.Admin.Access, Code(), "Third");
        var role = await _api.CreateRoleAsync(w.Admin.Access, "Third-" + Guid.NewGuid().ToString("N")[..8], StaffPermissions);
        var carol = await _api.CreateUserAsync(w.Admin.Access, Email("carol"), "Carol C", false, (c, role));
        var carolSession = await _api.LoginAsync(carol.Email, carol.Password);
        Assert.Equal(HttpStatusCode.NotFound, (await _api.GetAsync($"/api/v1/inventory/transfers/{id}", carolSession.Access)).StatusCode);
        Assert.Empty((await TestApi.ExpectAsync(await _api.GetAsync("/api/v1/inventory/transfers", carolSession.Access), HttpStatusCode.OK)).GetProperty("items").EnumerateArray());
    }

    [Fact]
    public async Task Approval_and_shipping_check_the_stock_is_really_there()
    {
        var w = await StartAsync();
        await StockAsync(w.Alice.Access, w.A, w.VariantId, 3);

        var tooMany = await RequestTransferAsync(w.Alice.Access, w.A, w.B, w.VariantId, 5);
        var refused = await StepAsync(w.Alice.Access, tooMany, "approve");
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Equal("INVENTORY.INSUFFICIENT_STOCK", await TestApi.ErrorCodeAsync(refused));

        var ok = await RequestTransferAsync(w.Alice.Access, w.A, w.B, w.VariantId, 3);
        await TestApi.ExpectAsync(await StepAsync(w.Alice.Access, ok, "approve"), HttpStatusCode.OK);
        await TestApi.ExpectAsync(await AdjustAsync(w.Alice.Access, w.A, w.VariantId, -2, "Loss"), HttpStatusCode.OK); // stock disappears after approval
        var shipRefused = await StepAsync(w.Alice.Access, ok, "ship");
        Assert.Equal("INVENTORY.INSUFFICIENT_STOCK", await TestApi.ErrorCodeAsync(shipRefused));
        Assert.Equal(1, await OnHandAsync(w.Alice.Access, w.A, w.VariantId));
        Assert.Equal("Approved", (await TransferAsync(w.Alice.Access, ok)).GetProperty("status").GetString());
    }

    [Fact]
    public async Task Cancelling_in_transit_puts_the_goods_back_and_only_the_source_may_do_it()
    {
        var w = await StartAsync();
        await StockAsync(w.Alice.Access, w.A, w.VariantId, 10);
        var id = await RequestTransferAsync(w.Alice.Access, w.A, w.B, w.VariantId, 4);
        await TestApi.ExpectAsync(await StepAsync(w.Alice.Access, id, "approve"), HttpStatusCode.OK);
        await TestApi.ExpectAsync(await StepAsync(w.Alice.Access, id, "ship"), HttpStatusCode.OK);
        Assert.Equal(6, await OnHandAsync(w.Alice.Access, w.A, w.VariantId));

        Assert.Equal(HttpStatusCode.Forbidden, (await StepAsync(w.Bob.Access, id, "cancel")).StatusCode); // stock returns to A, Bob holds nothing there
        var cancelled = await TestApi.ExpectAsync(await StepAsync(w.Alice.Access, id, "cancel"), HttpStatusCode.OK);

        Assert.Equal("Cancelled", cancelled.GetProperty("status").GetString());
        Assert.Equal(10, await OnHandAsync(w.Alice.Access, w.A, w.VariantId));
        Assert.Contains((await MovementsAsync(w.Alice.Access, w.A, w.VariantId)).EnumerateArray(), m => m.GetProperty("reason").GetString() == "TransferCancelled");
        Assert.Equal("INVENTORY.TRANSFER_STATE", await TestApi.ErrorCodeAsync(await StepAsync(w.Bob.Access, id, "receive")));
    }

    [Fact]
    public async Task A_requested_transfer_can_be_rejected_by_the_source_and_bad_requests_are_refused()
    {
        var w = await StartAsync();
        await StockAsync(w.Alice.Access, w.A, w.VariantId, 10);

        var id = await RequestTransferAsync(w.Bob.Access, w.A, w.B, w.VariantId, 1);
        var rejected = await TestApi.ExpectAsync(await _api.PostAsync($"/api/v1/inventory/transfers/{id}/reject", new { reason = "Keeping it" }, w.Alice.Access), HttpStatusCode.OK);
        Assert.Equal(("Rejected", "Keeping it"), (rejected.GetProperty("status").GetString(), rejected.GetProperty("cancelReason").GetString()));

        Assert.Equal(HttpStatusCode.BadRequest, (await _api.PostAsync("/api/v1/inventory/transfers",
            new { fromBranchId = w.A, toBranchId = w.A, lines = new[] { new { variantId = w.VariantId, quantity = 1 } } }, w.Alice.Access)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await _api.PostAsync("/api/v1/inventory/transfers",
            new { fromBranchId = w.A, toBranchId = w.B, lines = new[] { new { variantId = w.VariantId, quantity = 0 } } }, w.Alice.Access)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _api.PostAsync("/api/v1/inventory/transfers",
            new { fromBranchId = w.A, toBranchId = w.B, lines = new[] { new { variantId = Guid.NewGuid(), quantity = 1 } } }, w.Alice.Access)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _api.PostAsync("/api/v1/inventory/transfers",
            new { fromBranchId = Guid.NewGuid(), toBranchId = w.B, lines = new[] { new { variantId = w.VariantId, quantity = 1 } } }, w.Bob.Access)).StatusCode);
    }

    // ---- stock counts -------------------------------------------------------------------------------------

    private Task<HttpResponseMessage> CountStepAsync(string token, Guid id, string step) => _api.PostAsync($"/api/v1/inventory/counts/{id}/{step}", new { }, token);

    [Fact]
    public async Task A_stock_count_books_only_the_counted_differences_when_approved()
    {
        var w = await StartAsync();
        var (_, second, _) = await NewVariantAsync(w.Admin.Access);
        await StockAsync(w.Alice.Access, w.A, w.VariantId, 10);
        await StockAsync(w.Alice.Access, w.A, second, 4);

        var started = await TestApi.ExpectAsync(await _api.PostAsync("/api/v1/inventory/counts", new { branchId = w.A, note = "Monthly" }, w.Alice.Access), HttpStatusCode.Created);
        var id = started.GetProperty("summary").GetProperty("id").GetGuid();
        Assert.Equal(2, started.GetProperty("lines").GetArrayLength());
        Assert.StartsWith("SC-", started.GetProperty("summary").GetProperty("number").GetString());

        // A second open count in the same branch is refused.
        Assert.Equal("INVENTORY.COUNT_STATE", await TestApi.ErrorCodeAsync(await _api.PostAsync("/api/v1/inventory/counts", new { branchId = w.A }, w.Alice.Access)));

        // Nothing counted yet: cannot submit.
        Assert.Equal(HttpStatusCode.BadRequest, (await CountStepAsync(w.Alice.Access, id, "submit")).StatusCode);

        var saved = await TestApi.ExpectAsync(await _api.PutAsync($"/api/v1/inventory/counts/{id}/lines",
            new { lines = new[] { new { variantId = w.VariantId, countedQuantity = (int?)7 } } }, w.Alice.Access), HttpStatusCode.OK);
        Assert.Equal((1, 1, -3), (saved.GetProperty("summary").GetProperty("countedCount").GetInt32(), saved.GetProperty("summary").GetProperty("differenceLines").GetInt32(),
            saved.GetProperty("summary").GetProperty("totalDifference").GetInt32()));

        Assert.Equal(HttpStatusCode.BadRequest, (await _api.PutAsync($"/api/v1/inventory/counts/{id}/lines",
            new { lines = new[] { new { variantId = w.VariantId, countedQuantity = (int?)(-1) } } }, w.Alice.Access)).StatusCode);

        await TestApi.ExpectAsync(await CountStepAsync(w.Alice.Access, id, "submit"), HttpStatusCode.OK);
        Assert.Equal(10, await OnHandAsync(w.Alice.Access, w.A, w.VariantId)); // review does not change stock

        var approved = await TestApi.ExpectAsync(await CountStepAsync(w.Alice.Access, id, "approve"), HttpStatusCode.OK);
        Assert.Equal("Approved", approved.GetProperty("summary").GetProperty("status").GetString());
        Assert.Equal(7, await OnHandAsync(w.Alice.Access, w.A, w.VariantId));
        Assert.Equal(4, await OnHandAsync(w.Alice.Access, w.A, second)); // not counted, not changed

        var movement = Assert.Single((await MovementsAsync(w.Alice.Access, w.A, w.VariantId)).EnumerateArray(), m => m.GetProperty("reason").GetString() == "StockCount");
        Assert.Equal(-3, movement.GetProperty("quantityChange").GetInt32());
        Assert.Equal(id, movement.GetProperty("referenceId").GetGuid());

        Assert.Equal("INVENTORY.COUNT_STATE", await TestApi.ErrorCodeAsync(await CountStepAsync(w.Alice.Access, id, "approve")));
        // The branch is free for the next count.
        await TestApi.ExpectAsync(await _api.PostAsync("/api/v1/inventory/counts", new { branchId = w.A }, w.Alice.Access), HttpStatusCode.Created);
    }

    [Fact]
    public async Task A_count_can_be_reopened_or_cancelled_without_booking_anything_and_is_isolated_per_branch()
    {
        var w = await StartAsync();
        await StockAsync(w.Alice.Access, w.A, w.VariantId, 10);
        var started = await TestApi.ExpectAsync(await _api.PostAsync("/api/v1/inventory/counts", new { branchId = w.A }, w.Alice.Access), HttpStatusCode.Created);
        var id = started.GetProperty("summary").GetProperty("id").GetGuid();
        await TestApi.ExpectAsync(await _api.PutAsync($"/api/v1/inventory/counts/{id}/lines",
            new { lines = new[] { new { variantId = w.VariantId, countedQuantity = (int?)1 } } }, w.Alice.Access), HttpStatusCode.OK);
        await TestApi.ExpectAsync(await CountStepAsync(w.Alice.Access, id, "submit"), HttpStatusCode.OK);

        Assert.Equal(HttpStatusCode.NotFound, (await _api.GetAsync($"/api/v1/inventory/counts/{id}", w.Bob.Access)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await CountStepAsync(w.Bob.Access, id, "approve")).StatusCode);

        var reopened = await TestApi.ExpectAsync(await CountStepAsync(w.Alice.Access, id, "reopen"), HttpStatusCode.OK);
        Assert.Equal("InProgress", reopened.GetProperty("summary").GetProperty("status").GetString());
        var cancelled = await TestApi.ExpectAsync(await CountStepAsync(w.Alice.Access, id, "cancel"), HttpStatusCode.OK);
        Assert.Equal("Cancelled", cancelled.GetProperty("summary").GetProperty("status").GetString());
        Assert.Equal(10, await OnHandAsync(w.Alice.Access, w.A, w.VariantId));
    }

    [Fact]
    public async Task A_count_correction_below_the_reserved_units_is_refused()
    {
        var w = await StartAsync();
        await StockAsync(w.Alice.Access, w.A, w.VariantId, 10);

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<JuvoDbContext>();
            var level = await db.StockLevels.SingleAsync(l => l.BranchId == w.A && l.VariantId == w.VariantId, Ct);
            Assert.True(level.TryReserve(8)); // as an order will in Step 8
            await db.SaveChangesAsync(Ct);
        }

        var started = await TestApi.ExpectAsync(await _api.PostAsync("/api/v1/inventory/counts", new { branchId = w.A }, w.Alice.Access), HttpStatusCode.Created);
        var id = started.GetProperty("summary").GetProperty("id").GetGuid();
        await TestApi.ExpectAsync(await _api.PutAsync($"/api/v1/inventory/counts/{id}/lines",
            new { lines = new[] { new { variantId = w.VariantId, countedQuantity = (int?)5 } } }, w.Alice.Access), HttpStatusCode.OK);
        await TestApi.ExpectAsync(await CountStepAsync(w.Alice.Access, id, "submit"), HttpStatusCode.OK);

        var refused = await CountStepAsync(w.Alice.Access, id, "approve");

        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Equal("INVENTORY.INSUFFICIENT_STOCK", await TestApi.ErrorCodeAsync(refused));
        Assert.Equal(10, await OnHandAsync(w.Alice.Access, w.A, w.VariantId));

        // Stock that is reserved for orders cannot be written off by hand either.
        Assert.Equal(HttpStatusCode.Conflict, (await AdjustAsync(w.Alice.Access, w.A, w.VariantId, -5, "Loss")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await AdjustAsync(w.Alice.Access, w.A, w.VariantId, -2, "Loss")).StatusCode);
    }

    // ---- catalog interplay and concurrency ----------------------------------------------------------------

    [Fact]
    public async Task A_variant_with_stock_cannot_be_removed_until_the_stock_is_gone()
    {
        var w = await StartAsync();
        await StockAsync(w.Alice.Access, w.A, w.VariantId, 2);

        var refused = await _api.DeleteAsync($"/api/v1/catalog/products/{w.ProductId}/variants/{w.VariantId}", w.Admin.Access);
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Equal("INVENTORY.VARIANT_HAS_STOCK", await TestApi.ErrorCodeAsync(refused));

        await TestApi.ExpectAsync(await AdjustAsync(w.Alice.Access, w.A, w.VariantId, -2, "Loss"), HttpStatusCode.OK);
        Assert.Equal(HttpStatusCode.NoContent, (await _api.DeleteAsync($"/api/v1/catalog/products/{w.ProductId}/variants/{w.VariantId}", w.Admin.Access)).StatusCode);
    }

    [Fact]
    public async Task Parallel_adjustments_never_lose_an_update_and_the_ledger_always_adds_up()
    {
        var w = await StartAsync();
        await StockAsync(w.Alice.Access, w.A, w.VariantId, 1);

        var responses = await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => AdjustAsync(w.Alice.Access, w.A, w.VariantId, 1, "Found")));

        var ok = responses.Count(r => r.StatusCode == HttpStatusCode.OK);
        Assert.All(responses, r => Assert.Contains(r.StatusCode, new[] { HttpStatusCode.OK, HttpStatusCode.Conflict }));
        Assert.True(ok >= 1);

        var onHand = await OnHandAsync(w.Alice.Access, w.A, w.VariantId);
        Assert.Equal(1 + ok, onHand); // every accepted change is counted, every refused one is not
        var ledger = (await MovementsAsync(w.Alice.Access, w.A, w.VariantId)).EnumerateArray().Sum(m => m.GetProperty("quantityChange").GetInt32());
        Assert.Equal(onHand, ledger);
    }

    [Fact]
    public async Task Two_simultaneous_shipments_of_the_last_units_cannot_both_succeed()
    {
        var w = await StartAsync();
        await StockAsync(w.Alice.Access, w.A, w.VariantId, 5);
        var first = await RequestTransferAsync(w.Alice.Access, w.A, w.B, w.VariantId, 5);
        var second = await RequestTransferAsync(w.Alice.Access, w.A, w.B, w.VariantId, 5);
        await TestApi.ExpectAsync(await StepAsync(w.Alice.Access, first, "approve"), HttpStatusCode.OK);
        await TestApi.ExpectAsync(await StepAsync(w.Alice.Access, second, "approve"), HttpStatusCode.OK);

        var results = await Task.WhenAll(StepAsync(w.Alice.Access, first, "ship"), StepAsync(w.Alice.Access, second, "ship"));

        Assert.Equal(1, results.Count(r => r.StatusCode == HttpStatusCode.OK));
        Assert.Equal(0, await OnHandAsync(w.Alice.Access, w.A, w.VariantId));
    }

    [Fact]
    public async Task The_database_itself_refuses_negative_stock_and_ledger_rows_cannot_be_changed()
    {
        var w = await StartAsync();
        await StockAsync(w.Alice.Access, w.A, w.VariantId, 3);

        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<JuvoDbContext>();
        await Assert.ThrowsAnyAsync<Exception>(async () =>
            await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE StockLevels SET OnHand = -1 WHERE BranchId = {w.A} AND VariantId = {w.VariantId}", Ct));
        await Assert.ThrowsAnyAsync<Exception>(async () =>
            await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE StockLevels SET Reserved = OnHand + 1 WHERE BranchId = {w.A} AND VariantId = {w.VariantId}", Ct));

        var movement = await db.StockMovements.FirstAsync(m => m.BranchId == w.A && m.VariantId == w.VariantId, Ct);
        db.StockMovements.Remove(movement);
        await Assert.ThrowsAnyAsync<Exception>(async () => await db.SaveChangesAsync(Ct));
    }
}
