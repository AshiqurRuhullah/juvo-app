using System.Net;
using System.Text.Json;
using Juvo.Api.IntegrationTests.Orders;
using Juvo.Api.IntegrationTests.Storefront;
using Juvo.Api.IntegrationTests.Support;
using Microsoft.EntityFrameworkCore;
using static Juvo.Api.IntegrationTests.Promotions.PromoHelper;

namespace Juvo.Api.IntegrationTests.Promotions;

/// <summary>Pre-orders and backorders (module AA): the switch, the cart, the order and its queue, FIFO allocation after a receipt, no double allocation, the notification, the advance.</summary>
public class PreorderTests(PromoFactory factory) : IClassFixture<PromoFactory>
{
    private readonly PromoHelper _p = new(new OrderHelper(new StoreHelper(factory)), factory);

    private StoreHelper H => _p.H;

    private OrderHelper O => _p.O;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string Date(DateTimeOffset at) => DateOnly.FromDateTime(at.ToOffset(TimeSpan.FromHours(6)).DateTime).ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>A published product with no stock at all that allows pre-orders (ship date in ten days, 50 percent advance by default).</summary>
    private async Task<(string Admin, SeededProduct Product)> PreorderProductAsync(decimal price = 1000m, decimal advancePercent = 50m, bool allow = true)
    {
        var (admin, product) = await _p.ProductAsync(price, stock: 0);
        if (allow)
        {
            await H.JsonAsync(HttpMethod.Put, $"/api/v1/catalog/products/{product.Id}/preorder", HttpStatusCode.OK,
                new { allowPreorder = true, expectedShipDate = Date(_p.Now.AddDays(10)), advancePercent }, admin);
        }

        return (admin, product);
    }

    private async Task<JsonElement> ReceiveAsync(string admin, Guid variantId, int quantity)
    {
        var supplier = await H.JsonAsync(HttpMethod.Post, "/api/v1/suppliers", HttpStatusCode.Created, new { name = StoreHelper.Unique("Supplier"), contactName = "Mr Karim", phone = "01711000000", paymentTermsDays = 14 }, admin);
        var po = await H.JsonAsync(HttpMethod.Post, "/api/v1/purchasing/orders", HttpStatusCode.Created,
            new { branchId = await H.Api.BranchIdAsync("HO"), supplierId = supplier.GetProperty("id").GetGuid(), expectedDate = Date(_p.Now), lines = new[] { new { variantId, quantity, unitCost = 100m } } }, admin);
        var sent = await H.JsonAsync(HttpMethod.Post, $"/api/v1/purchasing/orders/{po.GetProperty("id").GetGuid()}/send", HttpStatusCode.OK, new { }, admin);
        return await H.JsonAsync(HttpMethod.Post, $"/api/v1/purchasing/orders/{sent.GetProperty("id").GetGuid()}/receive", HttpStatusCode.Created,
            new { note = "Delivery", lines = new[] { new { lineId = sent.GetProperty("lines")[0].GetProperty("id").GetGuid(), acceptedQuantity = quantity, rejectedQuantity = 0 } } }, admin);
    }

    private async Task<List<JsonElement>> QueueAsync(string admin, string state, Guid? variant = null)
    {
        var json = await H.JsonAsync(HttpMethod.Get, $"/api/v1/orders/preorders?state={state}&pageSize=100" + (variant is null ? string.Empty : $"&variantId={variant}"), HttpStatusCode.OK, bearer: admin);
        return [.. json.GetProperty("items").EnumerateArray()];
    }

    private Task<int> LogCountAsync(string number) =>
        O.WithDbAsync(db => db.NotificationLogs.CountAsync(n => n.Template == "order.preorder-ready" && n.DedupeKey != null && n.DedupeKey.StartsWith("preorder-ready:") && n.Body.Contains(number), Ct));

    // ---- switch and cart ----------------------------------------------------------------------------------

    [Fact]
    public async Task Only_a_product_that_allows_it_can_be_ordered_while_out_of_stock()
    {
        var (admin, plain) = await PreorderProductAsync(allow: false);
        await StoreHelper.ExpectCodeAsync(await H.SendAsync(HttpMethod.Post, "/api/v1/store/cart/items", new { variantId = plain.Variants[0].Id, quantity = 1 }), "CART.STOCK_INSUFFICIENT");

        var ship = Date(_p.Now.AddDays(10));
        var allowed = await _p.ProductAsync(900m, stock: 0);
        var settings = await H.JsonAsync(HttpMethod.Put, $"/api/v1/catalog/products/{allowed.Product.Id}/preorder", HttpStatusCode.OK, new { allowPreorder = true, expectedShipDate = ship, advancePercent = 25m }, admin);
        Assert.True(settings.GetProperty("allowPreorder").GetBoolean());
        Assert.True(settings.GetProperty("variants")[0].GetProperty("effectiveAllow").GetBoolean());
        Assert.Equal(25m, Num(settings.GetProperty("variants")[0], "effectiveAdvancePercent"));

        var token = await O.CartAsync(allowed.Product.Variants[0].Id, 2);
        var cart = await _p.CartAsync(token);
        var line = cart.GetProperty("lines")[0];
        Assert.True(line.GetProperty("isPreorder").GetBoolean());
        Assert.Equal(ship, Str(line, "preorderExpectedShipDate"));
        Assert.Contains("Pre-order", Str(line, "preorderLabelEn"), StringComparison.Ordinal);
        Assert.Contains("প্রি-অর্ডার", Str(line, "preorderLabelBn"), StringComparison.Ordinal);
        Assert.Equal(JsonValueKind.Null, line.GetProperty("problem").ValueKind);
        Assert.True(cart.GetProperty("hasPreorder").GetBoolean());
        Assert.Equal(1800m, Num(cart, "total"));
        Assert.Equal(450m, Num(cart, "preorderAdvance")); // 25 percent of 1800

        // The product page and card say so as well.
        var card = (await H.JsonAsync(HttpMethod.Get, $"/api/v1/store/products/{allowed.Product.Slug}", HttpStatusCode.OK)).GetProperty("card");
        Assert.True(card.GetProperty("isPreorder").GetBoolean());
        Assert.True(card.GetProperty("soldOut").GetBoolean());
        Assert.Equal(ship, Str(card, "preorderExpectedShipDate"));

        // A variant can override the product: forbid it for this variant.
        var variantId = allowed.Product.Variants[0].Id;
        await H.JsonAsync(HttpMethod.Put, $"/api/v1/catalog/products/{allowed.Product.Id}/variants/{variantId}/preorder", HttpStatusCode.OK, new { allowPreorder = false }, admin);
        await StoreHelper.ExpectCodeAsync(await H.SendAsync(HttpMethod.Post, "/api/v1/store/cart/items", new { variantId, quantity = 1 }), "CART.STOCK_INSUFFICIENT");

        // Bad settings are refused.
        await StoreHelper.ExpectCodeAsync(await H.SendAsync(HttpMethod.Put, $"/api/v1/catalog/products/{allowed.Product.Id}/preorder", new { allowPreorder = true, advancePercent = 150m }, admin), "GENERAL.VALIDATION");
        await StoreHelper.ExpectCodeAsync(await H.SendAsync(HttpMethod.Put, $"/api/v1/catalog/products/{allowed.Product.Id}/preorder", new { allowPreorder = true, expectedShipDate = Date(_p.Now.AddDays(-3)) }, admin), "GENERAL.VALIDATION");
    }

    [Fact]
    public async Task A_product_with_some_stock_is_sold_normally_and_limited_to_what_is_there()
    {
        var (admin, product) = await _p.ProductAsync(500m, stock: 1);
        await H.JsonAsync(HttpMethod.Put, $"/api/v1/catalog/products/{product.Id}/preorder", HttpStatusCode.OK, new { allowPreorder = true, advancePercent = 50m }, admin);
        var variant = product.Variants[0].Id;

        var token = await O.CartAsync(variant, 1);
        Assert.False((await _p.CartAsync(token)).GetProperty("lines")[0].GetProperty("isPreorder").GetBoolean());
        await StoreHelper.ExpectCodeAsync(await H.SendAsync(HttpMethod.Put, $"/api/v1/store/cart/items/{variant}", new { quantity = 2 }, cartToken: token), "CART.STOCK_INSUFFICIENT");
    }

    // ---- order, queue, tracking, invoice -----------------------------------------------------------------

    [Fact]
    public async Task A_pre_order_is_placed_without_a_reservation_waits_in_its_own_queue_and_cannot_be_confirmed_or_packed()
    {
        var (admin, product) = await PreorderProductAsync(1000m, 50m);
        var variant = product.Variants[0].Id;
        var token = await O.CartAsync(variant, 2);
        var quote = await _p.QuoteAsync(token);
        Assert.True(quote.GetProperty("hasPreorder").GetBoolean());
        Assert.True(quote.GetProperty("lines")[0].GetProperty("isPreorder").GetBoolean());
        Assert.Equal(1000m, Num(quote, "advanceRequired")); // 50 percent of 2000, through the usual advance mechanism
        Assert.Equal(1000m, Num(quote, "payNow"));

        var mobile = StoreHelper.NewMobile();
        var placed = await _p.PlaceCartAsync(token, mobile, expected: Num(quote, "grandTotal"));
        Assert.True(placed.GetProperty("hasPreorder").GetBoolean());
        var orderId = placed.GetProperty("orderId").GetGuid();
        var number = Str(placed, "number");

        Assert.Equal((0, 0), await O.StockAsync(variant)); // nothing reserved
        var order = await O.OrderAsync(admin, orderId);
        Assert.Equal("Waiting", Str(order, "preorderState"));
        var item = order.GetProperty("items")[0];
        Assert.True(item.GetProperty("isPreorder").GetBoolean());
        Assert.True(item.GetProperty("waitingForStock").GetBoolean());
        Assert.Equal(1000m, Num(order.GetProperty("money"), "advanceRequired"));

        // Tracking shows it too.
        var track = await H.JsonAsync(HttpMethod.Post, "/api/v1/store/orders/track", HttpStatusCode.OK, new { number, mobile });
        Assert.Equal("Waiting", Str(track, "preorderState"));
        Assert.True(track.GetProperty("items")[0].GetProperty("isPreorder").GetBoolean());
        Assert.Contains("ships", Str(track.GetProperty("items")[0], "preorderLabelEn"), StringComparison.Ordinal);

        // Its own queue, with filters.
        var waiting = await QueueAsync(admin, "Waiting", variant);
        var entry = Assert.Single(waiting, w => Str(w, "number") == number);
        Assert.Equal(2, entry.GetProperty("lines")[0].GetProperty("waiting").GetInt32());
        Assert.DoesNotContain(await QueueAsync(admin, "Ready"), w => Str(w, "number") == number);

        // It cannot be confirmed or packed before stock is allocated, but it can be cancelled (and nothing was held).
        await StoreHelper.ExpectCodeAsync(await O.TryActAsync(admin, orderId, "confirm", new { advanceReceived = true }), "PREORDER.AWAITING_STOCK");
        await StoreHelper.ExpectCodeAsync(await O.TryActAsync(admin, orderId, "pack"), "ORDER.INVALID_TRANSITION"); // it was never confirmed, so it cannot be packed either
        await O.ActAsync(admin, orderId, "cancel", new { reason = "customer changed mind" });
        Assert.Equal((0, 0), await O.StockAsync(variant));
        Assert.DoesNotContain(await QueueAsync(admin, "Waiting"), w => Str(w, "number") == number);
    }

    // ---- allocation ---------------------------------------------------------------------------------------

    [Fact]
    public async Task A_purchase_receipt_serves_waiting_pre_orders_oldest_first_and_the_customer_is_told_once()
    {
        var (admin, product) = await PreorderProductAsync(1000m, 50m);
        var variant = product.Variants[0].Id;
        var first = await O.PlaceAsync(variant, 2);
        var second = await O.PlaceAsync(variant, 2);
        var third = await O.PlaceAsync(variant, 2);
        var numbers = new[] { Str(first, "number"), Str(second, "number"), Str(third, "number") };

        await ReceiveAsync(admin, variant, 5); // enough for two whole orders; the third (2 units) must wait for one more unit

        var ready = await QueueAsync(admin, "Ready", variant);
        Assert.Equal([numbers[0], numbers[1]], ready.Select(r => Str(r, "number")).Order().ToArray());
        var waiting = await QueueAsync(admin, "Waiting", variant);
        Assert.Equal(numbers[2], Str(Assert.Single(waiting), "number"));
        Assert.Equal((5, 4), await O.StockAsync(variant));

        // The order is normal now: confirm (advance received), pack, stock leaves the shelf.
        var id = first.GetProperty("orderId").GetGuid();
        Assert.Equal("Ready", Str(await O.OrderAsync(admin, id), "preorderState"));
        await O.ActAsync(admin, id, "confirm", new { advanceReceived = true });
        await O.ActAsync(admin, id, "pack");
        Assert.Equal((3, 2), await O.StockAsync(variant));

        // The customer was told: one message per channel, once, for each served order and not for the waiting one.
        var firstLogs = await LogCountAsync(numbers[0]);
        Assert.True(firstLogs >= 1);
        Assert.True(await LogCountAsync(numbers[1]) >= 1);
        Assert.Equal(0, await LogCountAsync(numbers[2]));

        // Running the allocation again changes nothing and sends nothing again.
        var again = await H.JsonAsync(HttpMethod.Post, "/api/v1/orders/preorders/allocate", HttpStatusCode.OK, new { }, admin);
        Assert.Equal(0, again.GetProperty("linesAllocated").GetInt32());
        Assert.Equal(firstLogs, await LogCountAsync(numbers[0]));

        // One more unit arrives: the last order is served (the unit that was left over plus the new one) by the staff action as well.
        await H.SetStockChangeAsync(admin, variant, 1, "Correction");
        var manual = await H.JsonAsync(HttpMethod.Post, "/api/v1/orders/preorders/allocate", HttpStatusCode.OK, new { variantId = variant }, admin);
        Assert.Equal(1, manual.GetProperty("linesAllocated").GetInt32());
        Assert.Equal(1, manual.GetProperty("ordersReady").GetInt32());
        Assert.Empty(await QueueAsync(admin, "Waiting", variant));
        Assert.True(await LogCountAsync(numbers[2]) >= 1);
    }

    [Fact]
    public async Task The_queue_is_strictly_first_in_first_out_a_younger_order_does_not_jump_ahead()
    {
        var (admin, product) = await PreorderProductAsync();
        var variant = product.Variants[0].Id;
        var big = await O.PlaceAsync(variant, 3);
        var small = await O.PlaceAsync(variant, 1);

        await H.SetStockChangeAsync(admin, variant, 2, "OpeningStock");
        var result = await H.JsonAsync(HttpMethod.Post, "/api/v1/orders/preorders/allocate", HttpStatusCode.OK, new { variantId = variant }, admin);
        Assert.Equal(0, result.GetProperty("linesAllocated").GetInt32()); // the oldest needs 3, only 2 exist: nobody is served
        Assert.Equal((2, 0), await O.StockAsync(variant));
        Assert.Equal(2, (await QueueAsync(admin, "Waiting", variant)).Count);

        await H.SetStockChangeAsync(admin, variant, 2, "OpeningStock");
        var served = await H.JsonAsync(HttpMethod.Post, "/api/v1/orders/preorders/allocate", HttpStatusCode.OK, new { variantId = variant }, admin);
        Assert.Equal(2, served.GetProperty("linesAllocated").GetInt32());
        Assert.Contains(served.GetProperty("readyOrderIds").EnumerateArray(), x => x.GetGuid() == big.GetProperty("orderId").GetGuid());
        Assert.Contains(served.GetProperty("readyOrderIds").EnumerateArray(), x => x.GetGuid() == small.GetProperty("orderId").GetGuid());
        Assert.Equal((4, 4), await O.StockAsync(variant));
    }

    [Fact]
    public async Task Stock_is_never_allocated_twice_when_two_staff_actions_run_at_the_same_moment()
    {
        var (admin, product) = await PreorderProductAsync();
        var variant = product.Variants[0].Id;
        var a = await O.PlaceAsync(variant, 2);
        var b = await O.PlaceAsync(variant, 2);
        await H.SetStockChangeAsync(admin, variant, 2, "OpeningStock");

        var attempts = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => H.SendAsync(HttpMethod.Post, "/api/v1/orders/preorders/allocate", new { variantId = variant }, admin)));
        Assert.All(attempts, x => Assert.Equal(HttpStatusCode.OK, x.StatusCode));

        Assert.Equal((2, 2), await O.StockAsync(variant)); // exactly one order took the two units
        var ready = await QueueAsync(admin, "Ready", variant);
        var waiting = await QueueAsync(admin, "Waiting", variant);
        Assert.Single(ready);
        Assert.Single(waiting);
        Assert.Equal(Str(a, "number"), Str(ready[0], "number")); // the older one
        Assert.Equal(Str(b, "number"), Str(waiting[0], "number"));
    }

    [Fact]
    public async Task A_receipt_and_a_staff_action_at_the_same_moment_do_not_double_allocate()
    {
        var (admin, product) = await PreorderProductAsync();
        var variant = product.Variants[0].Id;
        var a = await O.PlaceAsync(variant, 3);
        var b = await O.PlaceAsync(variant, 3);

        var receipt = ReceiveAsync(admin, variant, 3);
        var staff = Task.WhenAll(Enumerable.Range(0, 3).Select(_ => H.SendAsync(HttpMethod.Post, "/api/v1/orders/preorders/allocate", new { }, admin)));
        await Task.WhenAll(receipt, staff);

        var (onHand, reserved) = await O.StockAsync(variant);
        Assert.Equal(3, onHand);
        Assert.Equal(3, reserved);
        Assert.Single(await QueueAsync(admin, "Ready", variant));
        Assert.Equal(Str(a, "number"), Str((await QueueAsync(admin, "Ready", variant))[0], "number"));
        Assert.Equal(Str(b, "number"), Str(Assert.Single(await QueueAsync(admin, "Waiting", variant)), "number"));
    }

    [Fact]
    public async Task The_invoice_of_a_pre_order_carries_the_flag_and_the_expected_ship_date()
    {
        var (admin, product) = await PreorderProductAsync(1000m, 50m);
        var variant = product.Variants[0].Id;
        var mobile = StoreHelper.NewMobile();
        var placed = await O.PlaceAsync(variant, 1, mobile);
        var id = placed.GetProperty("orderId").GetGuid();
        await ReceiveAsync(admin, variant, 1);
        await O.ActAsync(admin, id, "confirm", new { advanceReceived = true });

        var customer = await H.CustomerTokenAsync(mobile);
        var invoice = await H.JsonAsync(HttpMethod.Get, $"/api/v1/me/orders/{id}/invoice", HttpStatusCode.OK, bearer: customer);
        Assert.True(invoice.GetProperty("hasPreorder").GetBoolean());
        var line = invoice.GetProperty("lines")[0];
        Assert.True(line.GetProperty("isPreorder").GetBoolean());
        Assert.Equal(Date(_p.Now.AddDays(10)), Str(line, "preorderExpectedShipDate"));
        Assert.Contains("Pre-order", Str(line, "preorderLabelEn"), StringComparison.Ordinal);
    }

    // ---- branch and permission checks ---------------------------------------------------------------------

    [Fact]
    public async Task The_queue_and_the_allocation_respect_branches_and_permissions()
    {
        var (admin, product) = await PreorderProductAsync();
        var variant = product.Variants[0].Id;
        var placed = await O.PlaceAsync(variant, 1);
        var ho = await H.Api.BranchIdAsync("HO");
        var other = await H.Api.CreateBranchAsync(admin, "PB" + Guid.NewGuid().ToString("N")[..5].ToUpperInvariant(), "Pre-order branch");

        var viewerHere = await O.StaffTokenAsync(admin, ho, "orders.order.view");
        var viewerThere = await O.StaffTokenAsync(admin, other, "orders.order.view", "orders.order.edit");
        var nothing = await O.StaffTokenAsync(admin, ho, "catalog.product.view");

        Assert.Contains(await QueueAsync(viewerHere, "Waiting"), w => Str(w, "number") == Str(placed, "number"));
        Assert.DoesNotContain(await QueueAsync(viewerThere, "Waiting"), w => Str(w, "number") == Str(placed, "number")); // another branch's orders are not visible
        Assert.Equal(HttpStatusCode.Forbidden, (await H.SendAsync(HttpMethod.Get, "/api/v1/orders/preorders", bearer: nothing)).StatusCode);

        // Allocation needs orders.order.edit, in the branch asked for.
        Assert.Equal(HttpStatusCode.Forbidden, (await H.SendAsync(HttpMethod.Post, "/api/v1/orders/preorders/allocate", new { branchId = ho }, viewerHere)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await H.SendAsync(HttpMethod.Post, "/api/v1/orders/preorders/allocate", new { branchId = ho }, viewerThere)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await H.SendAsync(HttpMethod.Post, "/api/v1/orders/preorders/allocate", new { })).StatusCode);

        // The product switch needs the catalog permissions.
        Assert.Equal(HttpStatusCode.OK, (await H.SendAsync(HttpMethod.Get, $"/api/v1/catalog/products/{product.Id}/preorder", bearer: nothing)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await H.SendAsync(HttpMethod.Put, $"/api/v1/catalog/products/{product.Id}/preorder", new { allowPreorder = false }, nothing)).StatusCode);
    }
}
