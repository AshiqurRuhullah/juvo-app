using System.Net;
using System.Text.Json;
using Juvo.Api.IntegrationTests.Orders;
using Juvo.Api.IntegrationTests.Returns;
using Juvo.Api.IntegrationTests.Storefront;
using Juvo.Api.IntegrationTests.Support;
using Microsoft.Extensions.DependencyInjection;
using static Juvo.Api.IntegrationTests.Promotions.PromoHelper;

namespace Juvo.Api.IntegrationTests.Promotions;

/// <summary>One price everywhere (module Z): the cart, the checkout quote, the placed order, the invoice and a refund show the same numbers.</summary>
public class PricingConsistencyTests(PromoFactory factory) : IClassFixture<PromoFactory>
{
    private readonly PromoHelper _p = new(new OrderHelper(new StoreHelper(factory)), factory);

    private StoreHelper H => _p.H;

    private OrderHelper O => _p.O;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task The_cart_the_quote_the_order_the_invoice_and_a_refund_show_the_same_discounted_price()
    {
        var (admin, product) = await _p.ProductAsync(1000m);
        var variant = product.Variants[0];
        var promo = await _p.LiveAsync(admin, FlashSale("Eid flash", "Percent", 20m, [ProductTarget(product)], _p.Now.AddMinutes(-5), _p.Now.AddHours(1)));
        var promoId = promo.GetProperty("id").GetGuid();
        Assert.Equal("Active", Str(promo, "status"));

        // Cart
        var token = await O.CartAsync(variant.Id, 3);
        var cart = await _p.CartAsync(token);
        var line = cart.GetProperty("lines")[0];
        Assert.Equal(800m, Num(line, "unitPrice"));
        Assert.Equal(1000m, Num(line, "listPrice"));
        Assert.Equal(2400m, Num(line, "lineTotal"));
        Assert.Equal(2400m, Num(cart, "subtotal"));
        Assert.Equal(2400m, Num(cart, "total"));
        Assert.Equal(3000m, Num(cart, "listSubtotal"));
        Assert.Equal(600m, Num(cart.GetProperty("explanation"), "savings"));
        Assert.Equal("Eid flash", Str(line, "promotionLabelEn"));
        Assert.False(cart.GetProperty("priceChanged").GetBoolean()); // the price the line was added at is the price now

        // Checkout quote
        var quote = await _p.QuoteAsync(token);
        Assert.Equal(800m, Num(quote.GetProperty("lines")[0], "unitPrice"));
        Assert.Equal(2400m, Num(quote.GetProperty("lines")[0], "lineTotal"));
        Assert.Equal(2400m, Num(quote, "subtotal"));
        var grand = Num(quote, "grandTotal");
        Assert.Equal(2400m + Num(quote, "deliveryCharge"), grand);

        // The order is billed what the customer was shown (the expectedTotal guard accepts it).
        var mobile = StoreHelper.NewMobile();
        var placed = await _p.PlaceCartAsync(token, mobile, expected: grand);
        var orderId = placed.GetProperty("orderId").GetGuid();
        Assert.Equal(grand, Num(placed, "grandTotal"));
        var order = await O.OrderAsync(admin, orderId);
        var item = order.GetProperty("items")[0];
        Assert.Equal(800m, Num(item, "unitPrice"));
        Assert.Equal(1000m, Num(item, "listPrice"));
        Assert.Equal(2400m, Num(item, "lineTotal"));
        Assert.Equal(2400m, Num(order.GetProperty("money"), "subtotal"));
        Assert.Equal(grand, Num(order.GetProperty("money"), "grandTotal"));
        var adjustment = Assert.Single(item.GetProperty("adjustments").EnumerateArray(), a => Str(a, "sourceType") == "Promotion");
        Assert.Equal(promoId, adjustment.GetProperty("sourceId").GetGuid());
        Assert.Equal(600m, Num(adjustment, "amount"));

        // The invoice
        await O.ActAsync(admin, orderId, "confirm", new { advanceReceived = true });
        var customer = await H.CustomerTokenAsync(mobile);
        var invoice = await H.JsonAsync(HttpMethod.Get, $"/api/v1/me/orders/{orderId}/invoice", HttpStatusCode.OK, bearer: customer);
        Assert.Equal(2400m, Num(invoice, "subtotal"));
        Assert.Equal(grand, Num(invoice, "grandTotal"));
        Assert.Equal(800m, Num(invoice.GetProperty("lines")[0], "unitPrice"));
        Assert.Equal(1000m, Num(invoice.GetProperty("lines")[0], "listPrice"));

        // The promotion changes afterwards: the order and the invoice are snapshots and do not move.
        await H.JsonAsync(HttpMethod.Post, $"/api/v1/promotions/{promoId}/archive", HttpStatusCode.OK, new { }, admin);
        await H.ChangePriceAsync(admin, product, 1500m);
        var again = await O.OrderAsync(admin, orderId);
        Assert.Equal(800m, Num(again.GetProperty("items")[0], "unitPrice"));
        Assert.Equal(grand, Num(again.GetProperty("money"), "grandTotal"));
        var invoiceAgain = await H.JsonAsync(HttpMethod.Get, $"/api/v1/me/orders/{orderId}/invoice", HttpStatusCode.OK, bearer: customer);
        Assert.Equal(invoice.GetProperty("number").GetString(), Str(invoiceAgain, "number"));
        Assert.Equal(2400m, Num(invoiceAgain, "subtotal"));
        Assert.Equal(800m, Num(invoiceAgain.GetProperty("lines")[0], "unitPrice"));
    }

    [Fact]
    public async Task A_refund_of_a_discounted_line_pays_back_what_was_paid_for_it()
    {
        var (admin, product) = await _p.ProductAsync(1000m);
        var variant = product.Variants[0];
        await _p.LiveAsync(admin, FlashSale("Refund flash", "Percent", 20m, [ProductTarget(product)], _p.Now.AddMinutes(-5), _p.Now.AddHours(1)));
        var returns = new ReturnHelper(O);

        var delivered = await returns.DeliverAsync(admin, variant.Id, 2); // two units at 800
        var requested = await returns.RequestAsync(delivered, quantity: 1);
        var id = requested.GetProperty("id").GetGuid();
        var inspected = await returns.ToInspectedAsync(admin, id);

        Assert.Equal(800m, Num(inspected.GetProperty("items")[0], "refundAmount")); // not the list price of 1000
        var refunded = await returns.RefundAsync(admin, id);
        Assert.Equal(800m, Num(refunded.GetProperty("refund"), "total"));
    }

    [Fact]
    public async Task A_bundle_discount_is_shared_over_the_order_lines_and_the_refund_of_a_line_uses_its_net_value()
    {
        var (admin, a) = await _p.ProductAsync(500m);
        var b = await _p.MoreProductAsync(admin, 700m);
        var promo = await _p.LiveAsync(admin, Bundle("A and B combo", 1000m, ProductTarget(a, "BundleItem"), ProductTarget(b, "BundleItem")));

        var token = await _p.CartOfAsync((a.Variants[0].Id, 1), (b.Variants[0].Id, 1));
        var cart = await _p.CartAsync(token);
        Assert.Equal(1200m, Num(cart, "subtotal"));
        Assert.Equal(200m, Num(cart, "promotionDiscount"));
        Assert.Equal(0m, Num(cart, "discount"));
        Assert.Equal(1000m, Num(cart, "total"));

        var quote = await _p.QuoteAsync(token);
        Assert.Equal(200m, Num(quote, "promotionDiscount"));
        var grand = Num(quote, "grandTotal");

        // Place, deliver (the helper places single-variant orders, so deliver this one by hand), then return the 500 line.
        var mobile = StoreHelper.NewMobile();
        var placed = await _p.PlaceCartAsync(token, mobile, expected: grand);
        var orderId = placed.GetProperty("orderId").GetGuid();
        var order = await O.OrderAsync(admin, orderId);
        Assert.Equal(1200m, Num(order.GetProperty("money"), "subtotal"));
        Assert.Equal(200m, Num(order.GetProperty("money"), "promotionDiscount"));
        Assert.Equal(grand, Num(order.GetProperty("money"), "grandTotal"));
        Assert.Equal(200m, order.GetProperty("items").EnumerateArray().Sum(i => Num(i, "promotionDiscount")));
        Assert.All(order.GetProperty("items").EnumerateArray(), i => Assert.Contains(i.GetProperty("adjustments").EnumerateArray(), x => Str(x, "sourceType") == "Bundle" && x.GetProperty("sourceId").GetGuid() == promo.GetProperty("id").GetGuid()));

        await O.ActAsync(admin, orderId, "confirm", new { advanceReceived = true });
        var customer = await H.CustomerTokenAsync(mobile);
        var invoice = await H.JsonAsync(HttpMethod.Get, $"/api/v1/me/orders/{orderId}/invoice", HttpStatusCode.OK, bearer: customer);
        Assert.Equal(1200m, Num(invoice, "subtotal"));
        Assert.Equal(200m, Num(invoice, "discount"));
        Assert.Equal(200m, Num(invoice, "orderPromotionDiscount"));
        Assert.Equal(grand, Num(invoice, "grandTotal"));
        Assert.Equal(1200m, invoice.GetProperty("lines").EnumerateArray().Sum(l => Num(l, "lineTotal")));

        await O.ActAsync(admin, orderId, "pack");
        foreach (var step in new[] { "HandedToCourier", "InTransit", "OutForDelivery", "Delivered" })
        {
            await O.ActAsync(admin, orderId, "transition", new { to = step });
        }

        var returns = new ReturnHelper(O);
        var delivered = new DeliveredOrder(orderId, Str(placed, "number"), mobile, customer, a.Variants[0].Id, Num(order.GetProperty("money"), "deliveryCharge"));
        var items = await returns.EligibilityAsync(delivered);
        var lineA = items.GetProperty("items").EnumerateArray().First(i => Str(i, "sku") == a.Variants[0].Sku);
        Assert.Equal(416.67m, Num(lineA, "refundPerUnit"));
        var requested = await TestApi.ExpectAsync(await returns.TryRequestAsync(delivered, [new { orderItemId = lineA.GetProperty("orderItemId").GetGuid(), quantity = 1 }]), HttpStatusCode.Created);
        var inspected = await returns.ToInspectedAsync(admin, requested.GetProperty("id").GetGuid());
        // The bundle price is 1000 for 1200 of goods: the 500 line paid 500 * 1000 / 1200, rounded.
        Assert.Equal(416.67m, Num(inspected.GetProperty("items")[0], "refundAmount"));
    }

    [Fact]
    public async Task Buy_two_get_one_free_reaches_the_cart_the_quote_and_the_order()
    {
        var (admin, product) = await _p.ProductAsync(400m);
        await _p.LiveAsync(admin, BuyXGetY("Buy 2 get 1", 2, 1, 100m, ProductTarget(product, "Buy")));

        var token = await O.CartAsync(product.Variants[0].Id, 3);
        var cart = await _p.CartAsync(token);
        Assert.Equal(400m, Num(cart, "promotionDiscount"));
        Assert.Equal(800m, Num(cart, "total"));
        var quote = await _p.QuoteAsync(token);
        var placed = await _p.PlaceCartAsync(token, expected: Num(quote, "grandTotal"));
        var order = await O.OrderAsync(admin, placed.GetProperty("orderId").GetGuid());
        Assert.Equal(1200m, Num(order.GetProperty("money"), "subtotal"));
        Assert.Equal(400m, Num(order.GetProperty("money"), "promotionDiscount"));
        Assert.Equal(Num(quote, "grandTotal"), Num(order.GetProperty("money"), "grandTotal"));
    }

    [Fact]
    public async Task A_free_delivery_promotion_waives_the_charge_in_the_delivery_quote_the_checkout_quote_and_the_order()
    {
        var (admin, product) = await _p.ProductAsync(1000m);
        var variant = product.Variants[0];
        var low = await O.CartAsync(variant.Id, 1);
        var body = new { countryCode = "BD", districtId = await O.DistrictAsync() };
        var standard = await H.JsonAsync(HttpMethod.Post, "/api/v1/store/cart/delivery-quote", HttpStatusCode.OK, body, cartToken: low);
        Assert.True(Num(standard, "total") > 0m);

        await _p.LiveAsync(admin, FreeDelivery("Free delivery from 2500", 2500m));
        var stillPaid = await H.JsonAsync(HttpMethod.Post, "/api/v1/store/cart/delivery-quote", HttpStatusCode.OK, body, cartToken: low);
        Assert.Equal(Num(standard, "total"), Num(stillPaid, "total")); // 1000 is below the threshold

        var high = await O.CartAsync(variant.Id, 3);
        var free = await H.JsonAsync(HttpMethod.Post, "/api/v1/store/cart/delivery-quote", HttpStatusCode.OK, body, cartToken: high);
        Assert.Equal(0m, Num(free, "total"));
        Assert.True(free.GetProperty("isFree").GetBoolean());
        Assert.True(Num(free, "freeDeliveryWaiver") > 0m);
        Assert.Equal("Free delivery from 2500", Str(free, "promotionLabelEn"));

        var quote = await _p.QuoteAsync(high);
        Assert.Equal(0m, Num(quote, "deliveryCharge"));
        Assert.Equal(3000m, Num(quote, "grandTotal"));
        var placed = await _p.PlaceCartAsync(high, expected: 3000m);
        var order = await O.OrderAsync(admin, placed.GetProperty("orderId").GetGuid());
        Assert.Equal(0m, Num(order.GetProperty("money"), "deliveryCharge"));
        Assert.Equal(3000m, Num(order.GetProperty("money"), "grandTotal"));

        // The public delivery quote (items) reflects it too.
        var viaItems = await H.JsonAsync(HttpMethod.Post, "/api/v1/store/delivery/quote", HttpStatusCode.OK, new { countryCode = "BD", districtId = await O.DistrictAsync(), items = new[] { new { variantId = variant.Id, quantity = 3 } } });
        Assert.Equal(0m, Num(viaItems, "total"));
    }

    [Fact]
    public async Task A_festival_campaign_has_a_page_with_its_products_and_a_banner_only_while_it_runs()
    {
        var (admin, a) = await _p.ProductAsync(1000m);
        var b = await _p.MoreProductAsync(admin, 600m);
        var slug = StoreHelper.Unique("eid").ToLowerInvariant();
        var created = await _p.CreateAsync(admin, new
        {
            nameEn = "Eid campaign", nameBn = "ঈদ ক্যাম্পেইন", type = "FestivalCampaign", slug, titleEn = "Eid collection", titleBn = "ঈদ কালেকশন", bodyEn = "Dress up for Eid", bodyBn = "ঈদের সাজ",
            discountKind = "Percent", discountValue = 10m, bannerSlot = "HomeTop", labelEn = "Eid 10% off", labelBn = "ঈদ ১০% ছাড়",
            targets = new[] { ProductTarget(a), ProductTarget(b) },
        });
        var id = created.GetProperty("id").GetGuid();
        Assert.Equal("Draft", Str(created, "status"));

        // A draft has no page and no banner.
        await StoreHelper.ExpectCodeAsync(await H.SendAsync(HttpMethod.Get, $"/api/v1/store/campaigns/{slug}"), "PROMO.CAMPAIGN_NOT_FOUND");

        Assert.Equal(HttpStatusCode.OK, (await _p.UploadAsync(admin, id, "hero")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await _p.UploadAsync(admin, id, "banner")).StatusCode);
        await _p.ActivateAsync(admin, id);

        var page = await H.JsonAsync(HttpMethod.Get, $"/api/v1/store/campaigns/{slug}", HttpStatusCode.OK);
        Assert.Equal("Eid collection", Str(page, "titleEn"));
        Assert.Equal("ঈদ কালেকশন", Str(page, "titleBn"));
        Assert.NotNull(page.GetProperty("heroImageUrl").GetString());
        var products = page.GetProperty("products").EnumerateArray().ToList();
        Assert.Equal(2, products.Count);
        var cardA = products.Single(c => c.GetProperty("slug").GetString() == a.Slug);
        Assert.Equal(900m, Num(cardA, "price"));          // the campaign price
        Assert.Equal(1000m, Num(cardA, "regularPrice"));  // struck through
        Assert.Equal("Eid 10% off", Str(cardA.GetProperty("promotion"), "labelEn"));

        var banners = await H.JsonAsync(HttpMethod.Get, "/api/v1/store/banners?slot=HomeTop", HttpStatusCode.OK);
        Assert.Contains(banners.EnumerateArray(), x => x.GetProperty("promotionId").GetGuid() == id && x.GetProperty("slug").GetString() == slug);
        var other = await H.JsonAsync(HttpMethod.Get, "/api/v1/store/banners?slot=ShopTop", HttpStatusCode.OK);
        Assert.DoesNotContain(other.EnumerateArray(), x => x.GetProperty("promotionId").GetGuid() == id);

        await H.JsonAsync(HttpMethod.Post, $"/api/v1/promotions/{id}/deactivate", HttpStatusCode.OK, new { }, admin);
        await StoreHelper.ExpectCodeAsync(await H.SendAsync(HttpMethod.Get, $"/api/v1/store/campaigns/{slug}"), "PROMO.CAMPAIGN_NOT_FOUND");
        var gone = await H.JsonAsync(HttpMethod.Get, "/api/v1/store/banners?slot=HomeTop", HttpStatusCode.OK);
        Assert.DoesNotContain(gone.EnumerateArray(), x => x.GetProperty("promotionId").GetGuid() == id);
    }

    [Fact]
    public async Task Product_cards_and_details_carry_the_promotion_info_for_the_countdown()
    {
        var (admin, product) = await _p.ProductAsync(1000m);
        var ends = _p.Now.AddHours(2);
        await _p.LiveAsync(admin, FlashSale("Night flash", "FixedPrice", 650m, [ProductTarget(product)], _p.Now.AddMinutes(-1), ends));

        var detail = await H.JsonAsync(HttpMethod.Get, $"/api/v1/store/products/{product.Slug}", HttpStatusCode.OK);
        var card = detail.GetProperty("card");
        Assert.Equal(650m, Num(card, "price"));
        Assert.Equal(1000m, Num(card, "regularPrice"));
        Assert.True(card.GetProperty("onSale").GetBoolean());
        var info = card.GetProperty("promotion");
        Assert.Equal("Night flash", Str(info, "labelEn"));
        Assert.Equal("Night flash বাংলা", Str(info, "labelBn"));
        Assert.Equal(ends.ToUnixTimeSeconds(), info.GetProperty("endsAt").GetDateTimeOffset().ToUnixTimeSeconds());
        Assert.Equal(650m, Num(detail.GetProperty("variants")[0], "price"));
        Assert.Equal(1000m, Num(detail.GetProperty("variants")[0], "listPrice"));

        var list = await H.JsonAsync(HttpMethod.Get, "/api/v1/store/products?q=" + Uri.EscapeDataString(product.Name), HttpStatusCode.OK);
        Assert.Equal(650m, Num(list.GetProperty("items")[0], "price"));
    }

    // ---- the manual order desk -------------------------------------------------------------------------------

    private async Task<object> DeskBodyAsync(string sku, int quantity) => new
    {
        customerMobile = StoreHelper.NewMobile(), customerName = "Nadia Rahman", source = "Facebook",
        address = new { recipientName = "Nadia Rahman", countryCode = "BD", districtId = await O.DistrictAsync(), addressLine = "House 3, Road 9, Gulshan" },
        lines = new[] { new { sku, quantity } }, paymentMethod = "COD", confirmDuplicate = true,
    };

    [Fact]
    public async Task The_staff_order_desk_uses_the_same_engine_and_respects_the_channel_of_a_promotion()
    {
        var (admin, product) = await _p.ProductAsync(1000m);
        var sku = product.Variants[0].Sku;
        var staff = await O.StaffTokenAsync(admin, null, "orders.order.create", "orders.order.view");
        await _p.LiveAsync(admin, ProductDiscount("Web only", "Percent", 50m, [ProductTarget(product)], channels: "Web"));

        // A promotion for the web shop only does not apply on the desk.
        var plain = await H.JsonAsync(HttpMethod.Post, "/api/v1/orders/manual/quote", HttpStatusCode.OK, await DeskBodyAsync(sku, 2), staff);
        Assert.Equal(2000m, Num(plain, "subtotal"));
        Assert.Equal(0m, Num(plain, "promotionDiscount"));

        await _p.LiveAsync(admin, ProductDiscount("Everywhere", "Percent", 10m, [ProductTarget(product)], channels: "Web, SocialDesk"));
        var body = await DeskBodyAsync(sku, 2);
        var quote = await H.JsonAsync(HttpMethod.Post, "/api/v1/orders/manual/quote", HttpStatusCode.OK, body, staff);
        Assert.Equal(1800m, Num(quote, "subtotal"));
        Assert.Equal(900m, Num(quote.GetProperty("lines")[0], "unitPrice"));
        Assert.Equal(1000m, Num(quote.GetProperty("lines")[0], "listPrice"));

        var created = await H.JsonAsync(HttpMethod.Post, "/api/v1/orders/manual", HttpStatusCode.Created, body, staff);
        Assert.Equal(Num(quote, "grandTotal"), Num(created.GetProperty("money"), "grandTotal"));
        Assert.Equal(900m, Num(created.GetProperty("items")[0], "unitPrice"));
    }

    // ---- coupons and promotions ------------------------------------------------------------------------------

    [Fact]
    public async Task A_coupon_and_a_promotion_give_the_better_of_the_two_never_both_and_the_cart_says_why()
    {
        var (admin, product) = await _p.ProductAsync(1000m);
        var variant = product.Variants[0];
        await _p.LiveAsync(admin, ProductDiscount("Ten off", "Percent", 10m, [ProductTarget(product)]));

        // A small coupon: the promotion (100 off) is better.
        var small = StoreHelper.Unique("SMALL").ToUpperInvariant();
        await H.CouponAsync(admin, small, "Fixed", 50m);
        var token = await O.CartAsync(variant.Id, 1);
        var smallCart = await _p.ApplyCouponAsync(token, small);
        Assert.False(smallCart.GetProperty("coupon").GetProperty("applied").GetBoolean());
        Assert.False(string.IsNullOrWhiteSpace(smallCart.GetProperty("coupon").GetProperty("notAppliedReasonEn").GetString()));
        Assert.Equal(900m, Num(smallCart, "total"));
        Assert.Equal(0m, Num(smallCart, "discount"));

        // The checkout then bills the same and does not count a coupon use.
        var quote = await _p.QuoteAsync(token);
        var placed = await _p.PlaceCartAsync(token, expected: Num(quote, "grandTotal"));
        var order = await O.OrderAsync(admin, placed.GetProperty("orderId").GetGuid());
        Assert.Equal(900m, Num(order.GetProperty("money"), "subtotal"));
        Assert.Equal(0m, Num(order.GetProperty("money"), "couponDiscount"));
        Assert.Equal(JsonValueKind.Null, order.GetProperty("money").GetProperty("couponCode").ValueKind);

        // A big coupon: it wins and the promotion is dropped (they never combine).
        var big = StoreHelper.Unique("BIG").ToUpperInvariant();
        await H.CouponAsync(admin, big, "Fixed", 300m);
        var token2 = await O.CartAsync(variant.Id, 1);
        var bigCart = await _p.ApplyCouponAsync(token2, big);
        Assert.True(bigCart.GetProperty("coupon").GetProperty("applied").GetBoolean());
        Assert.Equal(300m, Num(bigCart, "discount"));
        Assert.Equal(0m, Num(bigCart, "promotionDiscount"));
        Assert.Equal(1000m, Num(bigCart.GetProperty("lines")[0], "unitPrice"));
        Assert.Equal(700m, Num(bigCart, "total"));
        var placed2 = await _p.PlaceCartAsync(token2, expected: Num(await _p.QuoteAsync(token2), "grandTotal"));
        var order2 = await O.OrderAsync(admin, placed2.GetProperty("orderId").GetGuid());
        Assert.Equal(300m, Num(order2.GetProperty("money"), "couponDiscount"));
        Assert.Equal(1000m, Num(order2.GetProperty("items")[0], "unitPrice"));
        Assert.Equal(700m, Num(order2.GetProperty("money"), "grandTotal") - Num(order2.GetProperty("money"), "deliveryCharge"));
    }

    [Fact]
    public async Task A_coupon_stacks_with_promotions_only_when_both_allow_it()
    {
        var (admin, product) = await _p.ProductAsync(1000m);
        var variant = product.Variants[0];
        await _p.LiveAsync(admin, ProductDiscount("Ten off stackable", "Percent", 10m, [ProductTarget(product)], stackMode: "StacksWithCoupon"));
        var stackable = StoreHelper.Unique("STACK").ToUpperInvariant();
        var created = await TestApi.ExpectAsync(await H.Api.PostAsync("/api/v1/promotions/coupons", new { code = stackable, kind = "Fixed", value = 50m, minOrderValue = 0m, isActive = true, stacksWithPromotions = true }, admin), HttpStatusCode.Created);
        Assert.True(created.GetProperty("stacksWithPromotions").GetBoolean());
        var plain = StoreHelper.Unique("PLAIN").ToUpperInvariant();
        await H.CouponAsync(admin, plain, "Fixed", 50m);

        var token = await O.CartAsync(variant.Id, 1);
        var both = await _p.ApplyCouponAsync(token, stackable);
        Assert.True(both.GetProperty("coupon").GetProperty("applied").GetBoolean());
        Assert.Equal(100m, Num(both, "listSubtotal") - Num(both, "subtotal"));
        Assert.Equal(850m, Num(both, "total")); // 1000 - 100 (promotion) - 50 (coupon)

        var refused = await _p.ApplyCouponAsync(token, plain); // this coupon does not combine with promotions: the promotion is better
        Assert.False(refused.GetProperty("coupon").GetProperty("applied").GetBoolean());
        Assert.Equal(900m, Num(refused, "total"));
    }

    [Fact]
    public async Task Two_coupons_never_combine_in_the_engine()
    {
        var (_, product) = await _p.ProductAsync(1000m);
        await using var scope = factory.Services.CreateAsyncScope();
        var engine = scope.ServiceProvider.GetRequiredService<Juvo.Application.Pricing.IPricingEngine>();
        var result = await engine.PriceAsync(new Juvo.Application.Pricing.PricingRequest([new Juvo.Application.Pricing.PricingLineRequest(product.Variants[0].Id, 1)], CouponCodes: ["ONE", "TWO"]), Ct);
        Assert.True(result.IsFailure);
        Assert.Equal("PROMO.MULTIPLE_COUPONS", result.Error!.Code);
    }

    [Fact]
    public async Task A_promotion_usage_limit_is_counted_on_order_and_given_back_on_cancel()
    {
        var (admin, product) = await _p.ProductAsync(1000m);
        var promo = await _p.LiveAsync(admin, ProductDiscount("Limited", "Percent", 10m, [ProductTarget(product)], usageLimit: 1));
        var id = promo.GetProperty("id").GetGuid();

        var first = await O.PlaceAsync(product.Variants[0].Id, 1);
        Assert.Equal(1, (await H.JsonAsync(HttpMethod.Get, $"/api/v1/promotions/{id}", HttpStatusCode.OK, bearer: admin)).GetProperty("usedCount").GetInt32());

        // used up: the next customer pays the normal price
        var token = await O.CartAsync(product.Variants[0].Id, 1);
        Assert.Equal(1000m, Num((await _p.CartAsync(token)).GetProperty("lines")[0], "unitPrice"));

        await O.ActAsync(admin, first.GetProperty("orderId").GetGuid(), "cancel", new { reason = "test" });
        Assert.Equal(0, (await H.JsonAsync(HttpMethod.Get, $"/api/v1/promotions/{id}", HttpStatusCode.OK, bearer: admin)).GetProperty("usedCount").GetInt32());
        Assert.Equal(900m, Num((await _p.CartAsync(token)).GetProperty("lines")[0], "unitPrice"));
    }

    // ---- the staff API ---------------------------------------------------------------------------------------

    [Fact]
    public async Task Staff_create_edit_duplicate_list_activate_and_archive_promotions()
    {
        var (admin, product) = await _p.ProductAsync(1000m);
        var created = await _p.CreateAsync(admin, ProductDiscount("Weekend", "Percent", 15m, [ProductTarget(product)], starts: _p.Now.AddDays(1), ends: _p.Now.AddDays(3)));
        var id = created.GetProperty("id").GetGuid();
        Assert.Equal("Draft", Str(created, "status"));
        Assert.Equal("ProductDiscount", Str(created, "type"));
        Assert.Equal(product.Name, Str(created.GetProperty("targets")[0], "name"));
        Assert.EndsWith("+06:00", created.GetProperty("startsAt").GetRawText().Trim('"'), StringComparison.Ordinal); // answers come back in Dhaka time

        var active = await _p.ActivateAsync(admin, id);
        Assert.Equal("Scheduled", Str(active, "status")); // switched on, but it has not started

        var edited = await H.JsonAsync(HttpMethod.Put, $"/api/v1/promotions/{id}", HttpStatusCode.OK,
            ProductDiscount("Weekend plus", "Percent", 25m, [ProductTarget(product)], starts: _p.Now.AddDays(1), ends: _p.Now.AddDays(3)), admin);
        Assert.Equal("Weekend plus", Str(edited, "nameEn"));
        Assert.Equal(25m, Num(edited, "discountValue"));

        var copy = await H.JsonAsync(HttpMethod.Post, $"/api/v1/promotions/{id}/duplicate", HttpStatusCode.Created, new { }, admin);
        Assert.Equal("Draft", Str(copy, "status"));
        Assert.Equal("Weekend plus (copy)", Str(copy, "nameEn"));
        Assert.Equal(1, copy.GetProperty("targets").GetArrayLength());

        var scheduled = await H.JsonAsync(HttpMethod.Get, "/api/v1/promotions?status=Scheduled&pageSize=100", HttpStatusCode.OK, bearer: admin);
        Assert.Contains(scheduled.GetProperty("items").EnumerateArray(), i => i.GetProperty("id").GetGuid() == id);
        Assert.DoesNotContain(scheduled.GetProperty("items").EnumerateArray(), i => i.GetProperty("id").GetGuid() == copy.GetProperty("id").GetGuid());
        var searched = await H.JsonAsync(HttpMethod.Get, "/api/v1/promotions?search=Weekend&type=ProductDiscount&pageSize=100", HttpStatusCode.OK, bearer: admin);
        Assert.True(searched.GetProperty("total").GetInt32() >= 2);

        var archived = await H.JsonAsync(HttpMethod.Delete, $"/api/v1/promotions/{id}", HttpStatusCode.OK, bearer: admin);
        Assert.Equal("Archived", Str(archived, "status"));
        await StoreHelper.ExpectCodeAsync(await H.SendAsync(HttpMethod.Put, $"/api/v1/promotions/{id}", ProductDiscount("x", "Percent", 5m, [ProductTarget(product)]), admin), "PROMO.ARCHIVED");
        await StoreHelper.ExpectCodeAsync(await H.SendAsync(HttpMethod.Post, $"/api/v1/promotions/{id}/activate", new { }, admin), "PROMO.ARCHIVED");
        Assert.Equal(HttpStatusCode.OK, (await H.SendAsync(HttpMethod.Get, $"/api/v1/promotions/{id}", bearer: admin)).StatusCode); // archived, never erased
    }

    [Fact]
    public async Task Bad_promotions_are_refused_with_clear_errors()
    {
        var (admin, product) = await _p.ProductAsync(1000m);
        async Task Refused(object body, string code = "GENERAL.VALIDATION") => await StoreHelper.ExpectCodeAsync(await H.SendAsync(HttpMethod.Post, "/api/v1/promotions", body, admin), code);

        await Refused(new { nameEn = "", type = "ProductDiscount", discountKind = "Percent", discountValue = 10m, targets = new[] { ProductTarget(product) } });
        await Refused(new { nameEn = "x", type = "ProductDiscount", discountKind = "Percent", discountValue = 150m, targets = new[] { ProductTarget(product) } });
        await Refused(new { nameEn = "x", type = "ProductDiscount", discountKind = "Percent", discountValue = 10m });                                                // no targets
        await Refused(new { nameEn = "x", type = "FlashSale", discountKind = "Percent", discountValue = 10m, targets = new[] { ProductTarget(product) } });          // a flash sale needs an end
        await Refused(new { nameEn = "x", type = "ProductDiscount", startsAt = _p.Now.AddDays(2), endsAt = _p.Now.AddDays(1), discountKind = "Percent", discountValue = 10m, targets = new[] { ProductTarget(product) } });
        await Refused(new { nameEn = "x", type = "Bundle", bundlePrice = 100m, targets = new[] { ProductTarget(product, "BundleItem") } });                          // one unit is not a bundle
        await Refused(new { nameEn = "x", type = "BuyXGetY", buyQuantity = 0, getQuantity = 1, targets = new[] { ProductTarget(product, "Buy") } });
        await Refused(new { nameEn = "x", type = "ProductDiscount", discountKind = "Percent", discountValue = 10m, targets = new[] { Target("Product", Guid.NewGuid()) } }, "PROMO.TARGET_NOT_FOUND");
        await Refused(new { nameEn = "x", type = "FestivalCampaign", slug = "BAD SLUG", titleEn = "T", targets = new[] { ProductTarget(product) } });

        var slug = StoreHelper.Unique("camp").ToLowerInvariant();
        await _p.CreateAsync(admin, new { nameEn = "c1", type = "FestivalCampaign", slug, titleEn = "T", targets = new[] { ProductTarget(product) } });
        await Refused(new { nameEn = "c2", type = "FestivalCampaign", slug, titleEn = "T", targets = new[] { ProductTarget(product) } }, "PROMO.SLUG_TAKEN");

        // an incomplete promotion cannot be switched on
        var draft = await _p.CreateAsync(admin, new { nameEn = "free", type = "FreeDelivery", minCartValue = 100m });
        var bundle = await _p.CreateAsync(admin, new { nameEn = "empty campaign", type = "FestivalCampaign", slug = StoreHelper.Unique("empty").ToLowerInvariant(), titleEn = "T" });
        await StoreHelper.ExpectCodeAsync(await H.SendAsync(HttpMethod.Post, $"/api/v1/promotions/{bundle.GetProperty("id").GetGuid()}/activate", new { }, admin), "PROMO.INCOMPLETE");
        Assert.Equal("Draft", Str(draft, "status"));
    }

    [Fact]
    public async Task Preview_prices_a_sample_cart_with_a_chosen_promotion_set_before_it_is_published()
    {
        var (admin, product) = await _p.ProductAsync(1000m);
        var sku = product.Variants[0].Sku;
        var draft = await _p.CreateAsync(admin, ProductDiscount("Not live yet", "Percent", 30m, [ProductTarget(product)]));
        var draftId = draft.GetProperty("id").GetGuid();
        var live = await _p.LiveAsync(admin, ProductDiscount("Live", "Percent", 10m, [ProductTarget(product)]));

        async Task<JsonElement> Preview(object body) => await H.JsonAsync(HttpMethod.Post, "/api/v1/promotions/preview", HttpStatusCode.OK, body, admin);

        var running = await Preview(new { lines = new[] { new { sku, quantity = 2 } } });   // no set: what runs now
        Assert.Equal(1800m, Num(running.GetProperty("totals"), "goodsTotal"));

        var chosen = await Preview(new { lines = new[] { new { sku, quantity = 2 } }, promotionIds = new[] { draftId } });   // the draft, as if it ran
        Assert.Equal(1400m, Num(chosen.GetProperty("totals"), "goodsTotal"));
        Assert.Equal(draftId, chosen.GetProperty("promotions")[0].GetProperty("id").GetGuid());
        Assert.Equal(700m, Num(chosen.GetProperty("lines")[0], "unitPrice"));
        Assert.Equal(600m, Num(chosen.GetProperty("explanation"), "savings"));

        var none = await Preview(new { lines = new[] { new { sku, quantity = 2 } }, promotionIds = Array.Empty<Guid>() });
        Assert.Equal(2000m, Num(none.GetProperty("totals"), "goodsTotal"));

        var both = await Preview(new { lines = new[] { new { sku, quantity = 1 } }, promotionIds = new[] { draftId, live.GetProperty("id").GetGuid() } });
        Assert.Equal(700m, Num(both.GetProperty("totals"), "goodsTotal")); // the lowest price wins; they do not stack

        // Preview saves and counts nothing.
        Assert.Equal(0, (await H.JsonAsync(HttpMethod.Get, $"/api/v1/promotions/{draftId}", HttpStatusCode.OK, bearer: admin)).GetProperty("usedCount").GetInt32());
        await StoreHelper.ExpectCodeAsync(await H.SendAsync(HttpMethod.Post, "/api/v1/promotions/preview", new { lines = new[] { new { sku = "NOPE", quantity = 1 } } }, admin), "GENERAL.VALIDATION");
    }

    [Fact]
    public async Task The_campaign_report_equals_a_manual_calculation_from_the_order_lines()
    {
        var (admin, product) = await _p.ProductAsync(1000m, stock: 30);
        var variant = product.Variants[0].Id;
        var promo = await _p.LiveAsync(admin, FlashSale("Report flash", "Percent", 20m, [ProductTarget(product)], _p.Now.AddMinutes(-1), _p.Now.AddHours(1)));
        var id = promo.GetProperty("id").GetGuid();

        var first = await O.PlaceAsync(variant, 2);   // 2 x 800 = 1600, discount 400
        var second = await O.PlaceAsync(variant, 1);  // 1 x 800 = 800,  discount 200
        var cancelled = await O.PlaceAsync(variant, 4); // cancelled: not counted
        await O.ActAsync(admin, cancelled.GetProperty("orderId").GetGuid(), "cancel", new { reason = "changed mind" });
        Assert.NotEqual(Guid.Empty, first.GetProperty("orderId").GetGuid());
        Assert.NotEqual(Guid.Empty, second.GetProperty("orderId").GetGuid());

        var report = await H.JsonAsync(HttpMethod.Get, $"/api/v1/promotions/{id}/report", HttpStatusCode.OK, bearer: admin);
        Assert.Equal(2, report.GetProperty("orders").GetInt32());
        Assert.Equal(3, report.GetProperty("units").GetInt32());
        Assert.Equal(2400m, Num(report, "revenue"));
        Assert.Equal(600m, Num(report, "discountCost"));

        var list = await H.JsonAsync(HttpMethod.Get, "/api/v1/promotions/report?type=FlashSale&pageSize=100", HttpStatusCode.OK, bearer: admin);
        var row = list.GetProperty("items").EnumerateArray().Single(i => i.GetProperty("promotionId").GetGuid() == id);
        Assert.Equal((2, 3, 2400m, 600m), (row.GetProperty("orders").GetInt32(), row.GetProperty("units").GetInt32(), Num(row, "revenue"), Num(row, "discountCost")));

        // A bundle: both lines carry the promotion; revenue is the line values after the promotion discount.
        var b = await _p.MoreProductAsync(admin, 700m);
        var bundle = await _p.LiveAsync(admin, Bundle("Report bundle", 1000m, ProductTarget(product, "BundleItem"), ProductTarget(b, "BundleItem")));
        // the flash sale also runs on the first product: the line is 800 and 700, so the bundle (1000) saves 500
        var token = await _p.CartOfAsync((variant, 1), (b.Variants[0].Id, 1));
        await _p.PlaceCartAsync(token);
        var bundleReport = await H.JsonAsync(HttpMethod.Get, $"/api/v1/promotions/{bundle.GetProperty("id").GetGuid()}/report", HttpStatusCode.OK, bearer: admin);
        Assert.Equal(1, bundleReport.GetProperty("orders").GetInt32());
        Assert.Equal(2, bundleReport.GetProperty("units").GetInt32());
        Assert.Equal(500m, Num(bundleReport, "discountCost"));
        Assert.Equal(800m + 700m - 500m, Num(bundleReport, "revenue"));
    }

    [Fact]
    public async Task Promotion_endpoints_need_their_permission()
    {
        var (admin, product) = await _p.ProductAsync(1000m);
        var viewer = await O.StaffTokenAsync(admin, null, "promotions.promotion.view");
        var coupons = await O.StaffTokenAsync(admin, null, "promotions.coupon.view", "promotions.coupon.create");
        var body = ProductDiscount("Perm", "Percent", 10m, [ProductTarget(product)]);

        Assert.Equal(HttpStatusCode.OK, (await H.SendAsync(HttpMethod.Get, "/api/v1/promotions", bearer: viewer)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await H.SendAsync(HttpMethod.Post, "/api/v1/promotions", body, viewer)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await H.SendAsync(HttpMethod.Get, "/api/v1/promotions", bearer: coupons)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await H.SendAsync(HttpMethod.Post, "/api/v1/promotions/preview", new { lines = new[] { new { sku = product.Variants[0].Sku, quantity = 1 } } }, coupons)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await H.SendAsync(HttpMethod.Get, "/api/v1/promotions")).StatusCode);

        var created = await _p.CreateAsync(admin, body);
        var id = created.GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.Forbidden, (await H.SendAsync(HttpMethod.Post, $"/api/v1/promotions/{id}/activate", new { }, viewer)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await H.SendAsync(HttpMethod.Delete, $"/api/v1/promotions/{id}", bearer: viewer)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await H.SendAsync(HttpMethod.Get, $"/api/v1/promotions/{id}/report", bearer: coupons)).StatusCode);
    }
}
