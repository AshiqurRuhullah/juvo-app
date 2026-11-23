using System.Net;
using System.Text.Json;
using Juvo.Api.IntegrationTests.Orders;
using Juvo.Api.IntegrationTests.Storefront;
using Juvo.Api.IntegrationTests.Support;
using Juvo.Application.Integrations;
using Juvo.Application.Payments;
using Juvo.Domain.Integrations;
using Microsoft.EntityFrameworkCore;

namespace Juvo.Api.IntegrationTests.Platform;

/// <summary>Outgoing webhooks (FR-AF05) against a real listener on this machine: signed delivery, fan-out from the outbox, retry with backoff, failure state, redelivery.</summary>
public class WebhooksTests(WebhookFactory factory) : IClassFixture<WebhookFactory>
{
    private readonly IntegrationsHelper _i = new(factory);
    private readonly OrderHelper _o = new(new StoreHelper(factory));

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private StoreHelper Store => _i.Store;

    private async Task<(string Admin, SeededProduct Product)> ProductAsync(int stock = 20)
    {
        var admin = await Store.AdminAsync();
        var category = await Store.CategoryAsync(admin);
        var product = await Store.ProductAsync(admin, category.Id, StoreHelper.Unique("Hook Tee"), 800m, [("Red", "M", stock)], weightGrams: 300);
        return (admin, product);
    }

    /// <summary>Removes the other tests' endpoints, so this test's listener is the only subscriber.</summary>
    private async Task ClearEndpointsAsync() => await _i.WithDbAsync(async db =>
    {
        await db.WebhookEndpoints.ExecuteDeleteAsync(Ct);
        return 0;
    });

    // ---- management ------------------------------------------------------------------------------------------

    [Fact]
    public async Task Creating_an_endpoint_shows_the_secret_once_and_stores_it_protected()
    {
        var admin = await Store.AdminAsync();
        using var listener = new WebhookListener();

        var (id, secret) = await _i.CreateWebhookAsync(admin, listener.Url, ["order.placed"], "Accounting");

        Assert.Matches("^whsec_[0-9a-f]{64}$", secret);
        foreach (var url in new[] { "/api/v1/integrations/webhooks", $"/api/v1/integrations/webhooks/{id}" })
        {
            var text = await (await _i.Api.GetAsync(url, admin)).Content.ReadAsStringAsync(Ct);
            Assert.DoesNotContain(secret, text, StringComparison.Ordinal);
            Assert.DoesNotContain("secretProtected", text, StringComparison.OrdinalIgnoreCase);
        }

        var stored = await _i.WithDbAsync(db => db.WebhookEndpoints.AsNoTracking().SingleAsync(e => e.Id == id, Ct));
        Assert.DoesNotContain(secret, stored.SecretProtected, StringComparison.Ordinal);
        Assert.Equal(secret, await _i.WithServiceAsync<ISecretProtector, string?>(p => Task.FromResult(p.Unprotect(stored.SecretProtected))));

        var audit = string.Concat(await _i.WithDbAsync(db => db.AuditLogs.AsNoTracking().Where(a => a.Entity == "WebhookEndpoint" && a.EntityId == id.ToString()).Select(a => a.NewValue).ToListAsync(Ct)));
        Assert.NotEmpty(audit);
        Assert.DoesNotContain(secret, audit, StringComparison.Ordinal);
        Assert.DoesNotContain(stored.SecretProtected, audit, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("noevents")]
    [InlineData("unknownevent")]
    [InlineData("pingevent")]
    [InlineData("noname")]
    [InlineData("nourl")]
    public async Task Invalid_webhook_requests_are_refused(string problem)
    {
        var admin = await Store.AdminAsync();
        var body = problem switch
        {
            "noevents" => new { name = "Hook name", url = "https://93.184.216.34/hook", events = Array.Empty<string>() },
            "unknownevent" => new { name = "Hook name", url = "https://93.184.216.34/hook", events = new[] { "order.deleted" } },
            "pingevent" => new { name = "Hook name", url = "https://93.184.216.34/hook", events = new[] { "ping" } },
            "noname" => new { name = "x", url = "https://93.184.216.34/hook", events = new[] { "order.placed" } },
            _ => new { name = "Hook name", url = string.Empty, events = new[] { "order.placed" } },
        };

        var response = await _i.Api.PostAsync("/api/v1/integrations/webhooks", body, admin);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task A_ping_is_signed_with_the_endpoint_secret_and_carries_the_event_and_delivery_headers()
    {
        var admin = await Store.AdminAsync();
        using var listener = new WebhookListener();
        var (id, secret) = await _i.CreateWebhookAsync(admin, listener.Url, ["order.placed"]);

        var delivery = await TestApi.ExpectAsync(await _i.Api.PostAsync($"/api/v1/integrations/webhooks/{id}/test", null, admin), HttpStatusCode.OK);

        Assert.Equal("Succeeded", delivery.GetProperty("status").GetString());
        Assert.Equal(200, delivery.GetProperty("httpStatus").GetInt32());
        Assert.Equal(1, delivery.GetProperty("attempt").GetInt32());
        var received = Assert.Single(listener.Received);
        Assert.Equal("ping", received.Event);
        Assert.Equal(delivery.GetProperty("id").GetGuid().ToString(), received.DeliveryId);
        Assert.Null(WebhookSignature.Check(secret, received.Signature, received.Body, DateTimeOffset.UtcNow));
        Assert.Matches("^t=\\d+,v1=[0-9a-f]{64}$", received.Signature);
        Assert.NotNull(WebhookSignature.Check("a-different-secret", received.Signature, received.Body, DateTimeOffset.UtcNow));
        Assert.Equal("ping", received.Json.GetProperty("type").GetString());
        Assert.Equal(delivery.GetProperty("eventId").GetGuid(), received.Json.GetProperty("id").GetGuid());
        Assert.Equal(delivery.GetProperty("payloadHash").GetString(), WebhookDelivering.HashPayload(received.Body));
    }

    [Fact]
    public async Task A_failed_ping_is_reported_with_a_scrubbed_reason_and_is_not_retried()
    {
        var admin = await Store.AdminAsync();
        using var listener = new WebhookListener { Behaviour = _ => (500, TimeSpan.Zero, null) };
        var (id, _) = await _i.CreateWebhookAsync(admin, listener.Url, ["order.placed"]);

        var delivery = await TestApi.ExpectAsync(await _i.Api.PostAsync($"/api/v1/integrations/webhooks/{id}/test", null, admin), HttpStatusCode.OK);

        Assert.Equal("Failed", delivery.GetProperty("status").GetString());
        Assert.Equal(500, delivery.GetProperty("httpStatus").GetInt32());
        Assert.Equal("HTTP 500", delivery.GetProperty("error").GetString());
        Assert.DoesNotContain("127.0.0.1", delivery.GetRawText(), StringComparison.Ordinal);
        Assert.Equal(1, listener.Count);
    }

    [Fact]
    public async Task A_redirect_is_not_followed_and_a_slow_receiver_is_given_up_on_after_five_seconds()
    {
        var admin = await Store.AdminAsync();
        using var target = new WebhookListener();
        using var redirecting = new WebhookListener { Behaviour = _ => (302, TimeSpan.Zero, target.Url) };
        using var slow = new WebhookListener { Behaviour = _ => (200, TimeSpan.FromSeconds(8), null) };
        var (redirectId, _) = await _i.CreateWebhookAsync(admin, redirecting.Url, ["order.placed"]);
        var (slowId, _) = await _i.CreateWebhookAsync(admin, slow.Url, ["order.placed"]);

        var redirected = await TestApi.ExpectAsync(await _i.Api.PostAsync($"/api/v1/integrations/webhooks/{redirectId}/test", null, admin), HttpStatusCode.OK);
        Assert.Equal("Failed", redirected.GetProperty("status").GetString());
        Assert.Equal(302, redirected.GetProperty("httpStatus").GetInt32());
        Assert.Contains("redirects are not followed", redirected.GetProperty("error").GetString(), StringComparison.Ordinal);
        Assert.Equal(0, target.Count);

        var started = DateTimeOffset.UtcNow;
        var timedOut = await TestApi.ExpectAsync(await _i.Api.PostAsync($"/api/v1/integrations/webhooks/{slowId}/test", null, admin), HttpStatusCode.OK);
        Assert.Equal("Failed", timedOut.GetProperty("status").GetString());
        Assert.Equal("No answer within 5 seconds.", timedOut.GetProperty("error").GetString());
        Assert.InRange((DateTimeOffset.UtcNow - started).TotalSeconds, 4.5, 7.5);
    }

    [Fact]
    public async Task Disabling_stops_new_deliveries_enabling_resumes_them_and_deleting_removes_the_log()
    {
        await ClearEndpointsAsync();
        var (admin, product) = await ProductAsync();
        using var listener = new WebhookListener();
        var (id, _) = await _i.CreateWebhookAsync(admin, listener.Url, ["order.placed"]);
        await TestApi.ExpectAsync(await _i.Api.PostAsync($"/api/v1/integrations/webhooks/{id}/disable", null, admin), HttpStatusCode.OK);

        await _o.PlaceAsync(product.Variants[0].Id, 1);
        await _i.ProcessOutboxAsync();
        await _i.DispatchAsync();
        Assert.Equal(0, listener.Count);

        await TestApi.ExpectAsync(await _i.Api.PostAsync($"/api/v1/integrations/webhooks/{id}/enable", null, admin), HttpStatusCode.OK);
        await _o.PlaceAsync(product.Variants[0].Id, 1);
        await _i.ProcessOutboxAsync();
        await _i.DispatchAsync();
        Assert.Equal(1, listener.Count);

        Assert.Equal(HttpStatusCode.NoContent, (await _i.Api.DeleteAsync($"/api/v1/integrations/webhooks/{id}", admin)).StatusCode);
        Assert.Equal(0, await _i.WithDbAsync(db => db.WebhookDeliveries.CountAsync(d => d.EndpointId == id, Ct)));
        Assert.Equal(HttpStatusCode.NotFound, (await _i.Api.GetAsync($"/api/v1/integrations/webhooks/{id}", admin)).StatusCode);
    }

    // ---- events ----------------------------------------------------------------------------------------------

    [Fact]
    public async Task Order_product_and_stock_events_reach_the_endpoint_signed_with_public_fields_and_no_personal_data()
    {
        await ClearEndpointsAsync();
        var admin = await Store.AdminAsync();
        using var listener = new WebhookListener();
        var (_, secret) = await _i.CreateWebhookAsync(admin, listener.Url);
        var category = await Store.CategoryAsync(admin);
        var product = await Store.ProductAsync(admin, category.Id, StoreHelper.Unique("Event Tee"), 800m, [("Red", "M", 6)], weightGrams: 300);
        var ho = await _i.Api.BranchIdAsync("HO");
        await TestApi.ExpectAsync(await _i.Api.PutAsync("/api/v1/inventory/reorder-levels", new { branchId = ho, variantId = product.Variants[0].Id, reorderLevel = 5 }, admin), HttpStatusCode.OK);
        var mobile = StoreHelper.NewMobile();

        var placed = await _o.PlaceAsync(product.Variants[0].Id, 2, mobile);
        var orderId = placed.GetProperty("orderId").GetGuid();
        await _o.ActAsync(admin, orderId, "confirm");
        await _i.ProcessOutboxAsync();
        await _i.DispatchAsync();

        var all = listener.Received.ToList();
        var types = all.Select(r => r.Event).ToList();
        Assert.Contains("product.published", types);
        Assert.Contains("order.placed", types);
        Assert.Contains("order.status_changed", types);
        Assert.Contains("stock.low", types);

        // The same body can be checked with the secret of the endpoint, and nothing in any body names the customer.
        Assert.All(all, r =>
        {
            Assert.Null(WebhookSignature.Check(secret, r.Signature, r.Body, DateTimeOffset.UtcNow));
            Assert.DoesNotContain(mobile, r.Body, StringComparison.Ordinal);
            Assert.DoesNotContain("Nadia", r.Body, StringComparison.Ordinal);
            Assert.DoesNotContain("Dhanmondi", r.Body, StringComparison.Ordinal);
            Assert.Equal(r.Event, r.Json.GetProperty("type").GetString());
            Assert.NotEqual(Guid.Empty, r.Json.GetProperty("id").GetGuid());
        });

        var staff = await _o.OrderAsync(admin, orderId);
        var orderEvent = all.First(r => r.Event == "order.placed" && r.Json.GetProperty("data").GetProperty("number").GetString() == placed.GetProperty("number").GetString());
        Assert.Equal(staff.GetProperty("money").GetProperty("grandTotal").GetDecimal(), orderEvent.Json.GetProperty("data").GetProperty("grandTotal").GetDecimal());
        Assert.Equal(orderId, orderEvent.Json.GetProperty("data").GetProperty("id").GetGuid());
        Assert.Equal("BDT", orderEvent.Json.GetProperty("data").GetProperty("currency").GetString());

        var status = all.First(r => r.Event == "order.status_changed" && r.Json.GetProperty("data").GetProperty("to").GetString() == "Confirmed");
        Assert.Equal("Order", status.Json.GetProperty("data").GetProperty("track").GetString());
        Assert.Equal("Placed", status.Json.GetProperty("data").GetProperty("from").GetString());
        Assert.Equal("Confirmed", status.Json.GetProperty("data").GetProperty("order").GetProperty("status").GetString());

        var published = all.First(r => r.Event == "product.published" && r.Json.GetProperty("data").GetProperty("slug").GetString() == product.Slug);
        Assert.Equal(product.Id, published.Json.GetProperty("data").GetProperty("id").GetGuid());
        Assert.DoesNotContain("cost", published.Body, StringComparison.OrdinalIgnoreCase);

        var low = all.First(r => r.Event == "stock.low" && r.Json.GetProperty("data").GetProperty("sku").GetString() == product.Variants[0].Sku);
        Assert.Equal(4, low.Json.GetProperty("data").GetProperty("available").GetInt32());
        Assert.Equal(5, low.Json.GetProperty("data").GetProperty("reorderLevel").GetInt32());
        Assert.Equal("HO", low.Json.GetProperty("data").GetProperty("branchCode").GetString());
    }

    [Fact]
    public async Task Stock_low_is_sent_once_per_fall_below_the_reorder_level_and_again_after_a_restock()
    {
        await ClearEndpointsAsync();
        var admin = await Store.AdminAsync();
        using var listener = new WebhookListener();
        await _i.CreateWebhookAsync(admin, listener.Url, ["stock.low"]);
        var category = await Store.CategoryAsync(admin);
        var product = await Store.ProductAsync(admin, category.Id, StoreHelper.Unique("Low Tee"), 800m, [("Red", "M", 10)], weightGrams: 300);
        var variant = product.Variants[0];
        var ho = await _i.Api.BranchIdAsync("HO");
        await TestApi.ExpectAsync(await _i.Api.PutAsync("/api/v1/inventory/reorder-levels", new { branchId = ho, variantId = variant.Id, reorderLevel = 5 }, admin), HttpStatusCode.OK);
        await _i.ProcessOutboxAsync();
        await _i.DispatchAsync();
        Assert.Equal(0, listener.Count); // 10 on hand, level 5: not low

        await _o.PlaceAsync(variant.Id, 6); // 4 available: low
        await _i.ProcessOutboxAsync();
        await _i.DispatchAsync();
        Assert.Equal(1, listener.Count);
        Assert.Equal(variant.Sku, listener.Received.Single().Json.GetProperty("data").GetProperty("sku").GetString());

        await _o.PlaceAsync(variant.Id, 1); // still low: no second alert
        await _i.ProcessOutboxAsync();
        await _i.DispatchAsync();
        Assert.Equal(1, listener.Count);

        await Store.SetStockChangeAsync(admin, variant.Id, 30, "OpeningStock"); // restocked: above the level again
        await _i.ProcessOutboxAsync();
        await _i.DispatchAsync();
        Assert.Equal(1, listener.Count);

        for (var n = 0; n < 3; n++)
        {
            await _o.PlaceAsync(variant.Id, 10); // 33 available before: falls to 3, a new alert
        }

        await _i.ProcessOutboxAsync();
        await _i.DispatchAsync();
        Assert.Equal(2, listener.Count);
        Assert.Equal(["stock.low", "stock.low"], listener.Received.Select(r => r.Event));
    }

    [Fact]
    public async Task Changing_a_published_product_sends_product_updated_and_an_endpoint_only_gets_the_events_it_chose()
    {
        await ClearEndpointsAsync();
        var (admin, product) = await ProductAsync();
        using var all = new WebhookListener();
        using var onlyOrders = new WebhookListener();
        await _i.CreateWebhookAsync(admin, all.Url, ["product.updated"]);
        await _i.CreateWebhookAsync(admin, onlyOrders.Url, ["order.placed"]);
        await _i.ProcessOutboxAsync();
        await _i.DispatchAsync();
        var before = all.Count;

        await Store.ChangePriceAsync(admin, product, 950m);
        await _i.ProcessOutboxAsync();
        await _i.DispatchAsync();

        var update = all.Received.Skip(before).Single();
        Assert.Equal("product.updated", update.Event);
        Assert.Equal(950m, update.Json.GetProperty("data").GetProperty("price").GetDecimal());
        Assert.Equal(0, onlyOrders.Count);
    }

    [Fact]
    public async Task Handling_the_same_outbox_message_twice_writes_one_delivery_per_endpoint()
    {
        await ClearEndpointsAsync();
        var (admin, product) = await ProductAsync();
        using var listener = new WebhookListener();
        var (id, _) = await _i.CreateWebhookAsync(admin, listener.Url, ["order.placed"]);
        await _o.PlaceAsync(product.Variants[0].Id, 1);
        await _i.ProcessOutboxAsync();
        var first = await _i.WithDbAsync(db => db.WebhookDeliveries.CountAsync(d => d.EndpointId == id, Ct));
        Assert.Equal(1, first);

        // At-least-once: the message is delivered to the handlers again (a worker crashed before it was marked done).
        var newest = await _i.WithDbAsync(db => db.OutboxMessages.Where(m => m.Type == "domain.OrderPlaced").OrderByDescending(m => m.CreatedAt).Select(m => m.Id).FirstAsync(Ct));
        await _i.WithDbAsync(db => db.OutboxMessages.Where(m => m.Id == newest).ExecuteUpdateAsync(s => s.SetProperty(m => m.Status, Juvo.Domain.Platform.OutboxStatus.Pending), Ct));
        await _i.ProcessOutboxAsync();

        Assert.Equal(1, await _i.WithDbAsync(db => db.WebhookDeliveries.CountAsync(d => d.EndpointId == id, Ct)));
        await _i.DispatchAsync();
        await _i.DispatchAsync();
        Assert.Equal(1, listener.Count);
    }

    // ---- retry, failure, redelivery --------------------------------------------------------------------------

    [Fact]
    public async Task A_failing_receiver_is_tried_five_times_with_backoff_then_the_delivery_is_failed_and_can_be_redelivered()
    {
        await ClearEndpointsAsync();
        var (admin, product) = await ProductAsync();
        using var listener = new WebhookListener { Behaviour = _ => (503, TimeSpan.Zero, null) };
        var (endpointId, _) = await _i.CreateWebhookAsync(admin, listener.Url, ["order.placed"]);
        await _o.PlaceAsync(product.Variants[0].Id, 1);
        await _i.ProcessOutboxAsync();

        await _i.DispatchAsync();
        var afterFirst = await _i.WithDbAsync(db => db.WebhookDeliveries.AsNoTracking().SingleAsync(d => d.EndpointId == endpointId, Ct));
        Assert.Equal(WebhookDeliveryStatus.Pending, afterFirst.Status);
        Assert.Equal(1, afterFirst.Attempt);
        Assert.Equal(503, afterFirst.HttpStatus);
        Assert.Equal("HTTP 503", afterFirst.Error);
        Assert.InRange((afterFirst.NextAttemptAt - DateTimeOffset.UtcNow).TotalSeconds, 20, 31);

        // Not due yet: nothing is sent.
        await _i.DispatchAsync();
        Assert.Equal(1, listener.Count);

        for (var attempt = 2; attempt <= 5; attempt++)
        {
            await _i.MakeDueAsync();
            await _i.DispatchAsync();
        }

        var failed = await _i.WithDbAsync(db => db.WebhookDeliveries.AsNoTracking().SingleAsync(d => d.EndpointId == endpointId, Ct));
        Assert.Equal(WebhookDeliveryStatus.Failed, failed.Status);
        Assert.Equal(5, failed.Attempt);
        Assert.Equal(5, listener.Count);
        Assert.Equal(5, listener.Received.Select(r => r.DeliveryId).Distinct().Count() * 5);
        Assert.Single(listener.Received.Select(r => r.Json.GetProperty("id").GetGuid()).Distinct());
        Assert.All(listener.Received, r => Assert.Equal(failed.PayloadHash, WebhookDelivering.HashPayload(r.Body)));

        // Failed deliveries are never sent again by the worker.
        await _i.MakeDueAsync();
        await _i.DispatchAsync();
        Assert.Equal(5, listener.Count);

        var listed = await TestApi.ExpectAsync(await _i.Api.GetAsync($"/api/v1/integrations/webhooks/deliveries?endpointId={endpointId}&status=Failed", admin), HttpStatusCode.OK);
        Assert.Equal(1, listed.GetProperty("total").GetInt32());
        Assert.Equal("order.placed", listed.GetProperty("items")[0].GetProperty("eventType").GetString());
        var endpoint = await TestApi.ExpectAsync(await _i.Api.GetAsync($"/api/v1/integrations/webhooks/{endpointId}", admin), HttpStatusCode.OK);
        Assert.Equal(1, endpoint.GetProperty("failedDeliveries").GetInt32());

        // The receiver is fixed; staff redeliver the same event.
        listener.Behaviour = _ => (200, TimeSpan.Zero, null);
        var redelivered = await TestApi.ExpectAsync(await _i.Api.PostAsync($"/api/v1/integrations/webhooks/deliveries/{failed.Id}/redeliver", null, admin), HttpStatusCode.OK);
        Assert.Equal("Pending", redelivered.GetProperty("status").GetString());
        Assert.Equal(0, redelivered.GetProperty("attempt").GetInt32());
        Assert.Equal(HttpStatusCode.Conflict, (await _i.Api.PostAsync($"/api/v1/integrations/webhooks/deliveries/{failed.Id}/redeliver", null, admin)).StatusCode);

        await _i.DispatchAsync();
        var done = await _i.WithDbAsync(db => db.WebhookDeliveries.AsNoTracking().SingleAsync(d => d.Id == failed.Id, Ct));
        Assert.Equal(WebhookDeliveryStatus.Succeeded, done.Status);
        Assert.Equal(1, done.Attempt);
        Assert.Equal(1, done.Redeliveries);
        Assert.Equal(6, listener.Count);
        Assert.Equal(failed.EventId, listener.Received.Last().Json.GetProperty("id").GetGuid());

        var detail = await TestApi.ExpectAsync(await _i.Api.GetAsync($"/api/v1/integrations/webhooks/deliveries/{failed.Id}", admin), HttpStatusCode.OK);
        Assert.Equal(listener.Received.Last().Body, detail.GetProperty("payload").GetString());

        var audit = await _i.WithDbAsync(db => db.AuditLogs.AsNoTracking().Where(a => a.Entity == "WebhookEndpoint" && a.EntityId == endpointId.ToString()).Select(a => a.NewValue).ToListAsync(Ct));
        Assert.Contains(audit, a => (a ?? string.Empty).Contains("redeliver", StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_delivery_list_filters_by_event_type_endpoint_and_status()
    {
        var admin = await Store.AdminAsync();
        using var listener = new WebhookListener();
        var (a, _) = await _i.CreateWebhookAsync(admin, listener.Url, ["order.placed"]);
        var (b, _) = await _i.CreateWebhookAsync(admin, listener.Url, ["order.placed"]);
        await TestApi.ExpectAsync(await _i.Api.PostAsync($"/api/v1/integrations/webhooks/{a}/test", null, admin), HttpStatusCode.OK);
        await TestApi.ExpectAsync(await _i.Api.PostAsync($"/api/v1/integrations/webhooks/{b}/test", null, admin), HttpStatusCode.OK);

        var onlyA = await TestApi.ExpectAsync(await _i.Api.GetAsync($"/api/v1/integrations/webhooks/deliveries?endpointId={a}", admin), HttpStatusCode.OK);
        Assert.Equal(1, onlyA.GetProperty("total").GetInt32());
        Assert.Equal("ping", onlyA.GetProperty("items")[0].GetProperty("eventType").GetString());
        Assert.Equal(1, onlyA.GetProperty("items")[0].GetProperty("maxAttempts").GetInt32() == 5 ? 1 : 0);

        var pings = await TestApi.ExpectAsync(await _i.Api.GetAsync($"/api/v1/integrations/webhooks/deliveries?eventType=ping&status=Succeeded&pageSize=100", admin), HttpStatusCode.OK);
        Assert.True(pings.GetProperty("total").GetInt32() >= 2);
        var none = await TestApi.ExpectAsync(await _i.Api.GetAsync($"/api/v1/integrations/webhooks/deliveries?endpointId={a}&status=Failed", admin), HttpStatusCode.OK);
        Assert.Equal(0, none.GetProperty("total").GetInt32());
        Assert.Equal(HttpStatusCode.NotFound, (await _i.Api.GetAsync($"/api/v1/integrations/webhooks/deliveries/{Guid.NewGuid()}", admin)).StatusCode);
    }

    [Fact]
    public async Task Rotating_the_secret_changes_the_signature_key_for_later_deliveries()
    {
        var admin = await Store.AdminAsync();
        using var listener = new WebhookListener();
        var (id, oldSecret) = await _i.CreateWebhookAsync(admin, listener.Url, ["order.placed"]);
        var rotated = await TestApi.ExpectAsync(await _i.Api.PostAsync($"/api/v1/integrations/webhooks/{id}/rotate-secret", null, admin), HttpStatusCode.OK);
        var newSecret = rotated.GetProperty("secret").GetString()!;

        await TestApi.ExpectAsync(await _i.Api.PostAsync($"/api/v1/integrations/webhooks/{id}/test", null, admin), HttpStatusCode.OK);

        var received = Assert.Single(listener.Received);
        Assert.NotEqual(oldSecret, newSecret);
        Assert.Null(WebhookSignature.Check(newSecret, received.Signature, received.Body, DateTimeOffset.UtcNow));
        Assert.NotNull(WebhookSignature.Check(oldSecret, received.Signature, received.Body, DateTimeOffset.UtcNow));
    }

    // ---- permissions -----------------------------------------------------------------------------------------

    [Fact]
    public async Task Only_staff_with_the_webhook_permissions_can_see_or_manage_webhooks()
    {
        var admin = await Store.AdminAsync();
        using var listener = new WebhookListener();
        var (id, _) = await _i.CreateWebhookAsync(admin, listener.Url, ["order.placed"]);
        var nobody = await _o.StaffTokenAsync(admin, null, "orders.order.view");
        var apiKeyAdmin = await _o.StaffTokenAsync(admin, null, "integrations.apikey.view", "integrations.apikey.manage");
        var viewer = await _o.StaffTokenAsync(admin, null, "integrations.webhook.view");
        var payload = new { name = "Hook name", url = listener.Url, events = new[] { "order.placed" } };

        foreach (var token in new[] { nobody, apiKeyAdmin })
        {
            Assert.Equal(HttpStatusCode.Forbidden, (await _i.Api.GetAsync("/api/v1/integrations/webhooks", token)).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await _i.Api.GetAsync("/api/v1/integrations/webhooks/deliveries", token)).StatusCode);
        }

        Assert.Equal(HttpStatusCode.OK, (await _i.Api.GetAsync("/api/v1/integrations/webhooks", viewer)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await _i.Api.GetAsync("/api/v1/integrations/webhooks/deliveries", viewer)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await _i.Api.PostAsync("/api/v1/integrations/webhooks", payload, viewer)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await _i.Api.PostAsync($"/api/v1/integrations/webhooks/{id}/test", null, viewer)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await _i.Api.PostAsync($"/api/v1/integrations/webhooks/{id}/rotate-secret", null, viewer)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await _i.Api.DeleteAsync($"/api/v1/integrations/webhooks/{id}", viewer)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await _i.Api.GetAsync("/api/v1/integrations/webhooks")).StatusCode);
        Assert.Equal(0, listener.Count);
    }
}
