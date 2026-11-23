using System.Net;
using System.Text.Json;
using Juvo.Api.IntegrationTests.Orders;
using Juvo.Api.IntegrationTests.Support;
using Juvo.Application.Marketing;
using Juvo.Application.Privacy;
using Juvo.Domain.Customers;
using Juvo.Domain.Notifications;
using Juvo.Domain.Platform;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Juvo.Api.IntegrationTests.Storefront;

/// <summary>Privacy tools (FR-AF06, FR-E08, NFR-010): consent history, data export and anonymisation, with a real database.</summary>
public class PrivacyTests(StoreFactory factory) : IClassFixture<StoreFactory>
{
    private readonly OrderHelper _o = new(new StoreHelper(factory));

    private StoreHelper H => _o.H;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed record Person(string Mobile, string Full, Guid Id, string Refresh, string Access, Guid OrderId, string OrderNumber, string Email, string Name, string TicketText, string ContactText, Guid TicketId, Guid VariantId);

    private static string Url(Guid id, string tail) => $"/api/v1/customers/{id}/{tail}";

    private async Task<string> StaffAsync(params string[] permissions) => await _o.StaffTokenAsync(await H.AdminAsync(), null, permissions);

    private async Task<(string Refresh, string Access)> SignInAsync(string mobile)
    {
        var request = await TestApi.ExpectAsync(await H.Api.PostAsync("/api/v1/auth/otp/request", new { mobile }), HttpStatusCode.Accepted);
        var verified = await TestApi.ExpectAsync(await H.Api.PostAsync("/api/v1/auth/otp/verify", new { mobile, code = request.GetProperty("devCode").GetString()! }), HttpStatusCode.OK);
        return (verified.GetProperty("refreshToken").GetString()!, verified.GetProperty("accessToken").GetString()!);
    }

    /// <summary>A customer with a profile, an address, a wishlist entry, consent, an order, a ticket and a contact message, all with recognisable text.</summary>
    private async Task<Person> SeedAsync(string admin, bool cancelOrder, bool invoice = false)
    {
        var tag = Guid.NewGuid().ToString("N")[..8];
        var mobile = StoreHelper.NewMobile();
        var email = $"person{tag}@example.com";
        var name = "Nadia Privacy" + tag;
        var category = await H.CategoryAsync(admin);
        var product = await H.ProductAsync(admin, category.Id, StoreHelper.Unique("Privacy Tee"), 700m, [("Red", "M", 20)], weightGrams: 300);
        var variant = product.Variants[0].Id;

        var placed = await _o.PlaceAsync(variant, 1, mobile);
        var orderId = placed.GetProperty("orderId").GetGuid();
        var number = placed.GetProperty("number").GetString()!;

        var (refresh, access) = await SignInAsync(mobile);
        await H.JsonAsync(HttpMethod.Put, "/api/v1/me", HttpStatusCode.OK, new { fullName = name, email }, access);
        var district = await _o.DistrictAsync();
        await H.JsonAsync(HttpMethod.Post, "/api/v1/me/addresses", HttpStatusCode.Created, new { label = "Home", recipientName = name, phone = "01712345678", districtId = district, addressLine = "House 9 Privacy Road " + tag }, access);
        Assert.Equal(HttpStatusCode.NoContent, (await H.SendAsync(HttpMethod.Post, "/api/v1/me/wishlist", new { productId = product.Id }, access)).StatusCode);
        await H.JsonAsync(HttpMethod.Put, "/api/v1/me/consent", HttpStatusCode.OK, new { type = "MarketingSms", granted = true }, access);

        var ticketText = "My private ticket text " + tag;
        var ticket = await H.JsonAsync(HttpMethod.Post, "/api/v1/me/tickets", HttpStatusCode.Created, new { subject = "Subject " + tag, message = ticketText, category = "other" }, access);
        var contactText = "My private contact text " + tag;
        await TestApi.ExpectAsync(await H.Api.PostAsync("/api/v1/store/content/contact", new { name, mobile, email, subject = "Hello " + tag, message = contactText }), HttpStatusCode.Accepted);

        var id = await _o.WithDbAsync(db => db.Customers.Where(c => c.Mobile == "+88" + mobile).Select(c => c.Id).SingleAsync(Ct));
        var ticketId = ticket.GetProperty("id").GetGuid();
        if (invoice)
        {
            await _o.ActAsync(admin, orderId, "confirm");
            await H.JsonAsync(HttpMethod.Post, $"/api/v1/billing/orders/{orderId}/invoice", HttpStatusCode.OK, new { }, admin);
        }

        if (cancelOrder)
        {
            await _o.ActAsync(admin, orderId, "cancel", new { reason = "Customer called" });
        }

        return new Person(mobile, "+88" + mobile, id, refresh, access, orderId, number, email, name, ticketText, contactText, ticketId, variant);
    }

    // ---- consent history --------------------------------------------------------------------------------------

    [Fact]
    public async Task The_consent_history_records_the_customer_and_the_unsubscribe_link_with_old_and_new_values()
    {
        var admin = await H.AdminAsync();
        var mobile = StoreHelper.NewMobile();
        var customer = await H.CustomerTokenAsync(mobile);
        var id = await _o.WithDbAsync(db => db.Customers.Where(c => c.Mobile == "+88" + mobile).Select(c => c.Id).SingleAsync(Ct));
        await H.JsonAsync(HttpMethod.Put, "/api/v1/me/consent", HttpStatusCode.OK, new { type = "MarketingEmail", granted = true }, customer);
        await H.JsonAsync(HttpMethod.Put, "/api/v1/me/consent", HttpStatusCode.OK, new { type = "MarketingSms", granted = true }, customer);
        await H.JsonAsync(HttpMethod.Put, "/api/v1/me/consent", HttpStatusCode.OK, new { type = "MarketingSms", granted = false }, customer);
        var token = factory.Services.GetRequiredService<IUnsubscribeTokens>().Create(id, NotificationChannel.Email, null);
        await H.JsonAsync(HttpMethod.Post, $"/api/v1/store/unsubscribe?token={token}", HttpStatusCode.OK);

        var history = await H.JsonAsync(HttpMethod.Get, Url(id, "consents/history"), HttpStatusCode.OK, bearer: admin);

        Assert.False(history.GetProperty("piiMasked").GetBoolean());
        var items = history.GetProperty("items").EnumerateArray().ToList();
        Assert.Equal(4, items.Count);
        Assert.Equal("MarketingEmail", items[0].GetProperty("type").GetString());          // newest first: the unsubscribe
        Assert.Equal("UnsubscribeLink", items[0].GetProperty("sourceKind").GetString());
        Assert.True(items[0].GetProperty("oldValue").GetBoolean());
        Assert.False(items[0].GetProperty("newValue").GetBoolean());
        Assert.Equal("Sms", items[1].GetProperty("channel").GetString());
        Assert.True(items[1].GetProperty("oldValue").GetBoolean());
        Assert.False(items[1].GetProperty("newValue").GetBoolean());
        Assert.Equal("Customer", items[1].GetProperty("sourceKind").GetString());
        Assert.Contains("WhatsApp", items[1].GetProperty("appliesTo").EnumerateArray().Select(a => a.GetString()));
        var first = items[3];
        Assert.Equal(JsonValueKind.Null, first.GetProperty("oldValue").ValueKind);
        Assert.Equal("customer-profile", first.GetProperty("source").GetString());

        // Staff without view-pii still see the history, with the address part hidden.
        var viewer = await StaffAsync("customers.customer.view");
        var masked = await H.JsonAsync(HttpMethod.Get, Url(id, "consents/history"), HttpStatusCode.OK, bearer: viewer);
        Assert.True(masked.GetProperty("piiMasked").GetBoolean());
        Assert.All(masked.GetProperty("items").EnumerateArray().Where(i => i.GetProperty("ipAddress").ValueKind != JsonValueKind.Null), i => Assert.Contains('*', i.GetProperty("ipAddress").GetString()!));
    }

    [Fact]
    public async Task The_privacy_endpoints_refuse_anonymous_customers_and_staff_without_the_permission()
    {
        var admin = await H.AdminAsync();
        var customer = await H.CustomerTokenAsync();
        var id = Guid.CreateVersion7();
        var viewer = await StaffAsync("customers.customer.view", "customers.customer.view-pii", "customers.customer.edit", "customers.customer.export");
        var exporter = await StaffAsync("customers.privacy.export");

        foreach (var url in new[] { Url(id, "privacy/export"), Url(id, "privacy/anonymise") })
        {
            Assert.Equal(HttpStatusCode.Unauthorized, (await H.SendAsync(HttpMethod.Post, url, new { })).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await H.SendAsync(HttpMethod.Post, url, new { }, customer)).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await H.SendAsync(HttpMethod.Post, url, new { }, viewer)).StatusCode);
        }

        Assert.Equal(HttpStatusCode.Forbidden, (await H.SendAsync(HttpMethod.Post, Url(id, "privacy/anonymise"), new { }, exporter)).StatusCode);   // export right is not anonymise right
        Assert.Equal(HttpStatusCode.Unauthorized, (await H.SendAsync(HttpMethod.Get, Url(id, "consents/history"))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await H.SendAsync(HttpMethod.Get, Url(id, "consents/history"), null, exporter)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await H.SendAsync(HttpMethod.Post, Url(id, "privacy/export"), new { }, admin)).StatusCode);
    }

    [Fact]
    public async Task Only_the_super_admin_holds_the_anonymise_right_by_default()
    {
        await H.AdminAsync();
        var roles = await _o.WithDbAsync(async db => await (from rp in db.RolePermissions
                                                           join p in db.Permissions on rp.PermissionId equals p.Id
                                                           join r in db.Roles on rp.RoleId equals r.Id
                                                           where p.Code.StartsWith("customers.privacy.") && r.IsSystem
                                                           select r.Name).Distinct().ToListAsync(Ct));

        Assert.Empty(roles);
        var exists = await _o.WithDbAsync(db => db.Permissions.CountAsync(p => p.Code == "customers.privacy.anonymise" || p.Code == "customers.privacy.export", Ct));
        Assert.Equal(2, exists);
    }

    // ---- export ----------------------------------------------------------------------------------------------------

    [Fact]
    public async Task The_export_holds_the_customers_data_only_and_is_audited_without_its_content()
    {
        var admin = await H.AdminAsync();
        var person = await SeedAsync(admin, cancelOrder: false);
        var other = await SeedAsync(admin, cancelOrder: false);
        var staff = await StaffAsync("customers.privacy.export");

        var response = await H.SendAsync(HttpMethod.Post, Url(person.Id, "privacy/export"), new { }, staff);
        var text = await response.Content.ReadAsStringAsync(Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        Assert.Contains("attachment", response.Content.Headers.ContentDisposition?.DispositionType ?? string.Empty);
        var json = JsonDocument.Parse(text).RootElement;
        foreach (var section in new[] { "export", "profile", "addresses", "consents", "orders", "invoices", "returns", "tickets", "contactMessages", "wishlist", "loyalty", "notifications", "marketing", "signIns" })
        {
            Assert.True(json.TryGetProperty(section, out _), $"missing section {section}");
        }

        Assert.Equal(person.Full, json.GetProperty("profile").GetProperty("mobile").GetString());
        Assert.Equal(person.Email, json.GetProperty("profile").GetProperty("email").GetString());
        Assert.Contains("House 9 Privacy Road", json.GetProperty("addresses")[0].GetProperty("addressLine").GetString());
        Assert.Equal(person.OrderNumber, json.GetProperty("orders")[0].GetProperty("order").GetProperty("number").GetString());
        Assert.Equal(1, json.GetProperty("orders")[0].GetProperty("lines").GetArrayLength());
        Assert.Equal(person.TicketText, json.GetProperty("tickets")[0].GetProperty("messages")[0].GetProperty("text").GetString());
        Assert.Equal(person.ContactText, json.GetProperty("contactMessages")[0].GetProperty("message").GetString());
        Assert.Equal(1, json.GetProperty("wishlist").GetArrayLength());
        Assert.True(json.GetProperty("consents").GetProperty("history").GetArrayLength() >= 1);
        Assert.True(json.GetProperty("marketing").GetProperty("sms").GetProperty("granted").GetBoolean());
        Assert.All(json.GetProperty("notifications").EnumerateArray(), n => Assert.False(n.TryGetProperty("body", out _) || n.TryGetProperty("subject", out _)));

        // Nothing of another customer.
        Assert.DoesNotContain(other.Mobile, text, StringComparison.Ordinal);
        Assert.DoesNotContain(other.OrderNumber, text, StringComparison.Ordinal);
        Assert.DoesNotContain(other.Email, text, StringComparison.Ordinal);
        Assert.DoesNotContain(other.TicketText, text, StringComparison.Ordinal);
        Assert.DoesNotContain(other.ContactText, text, StringComparison.Ordinal);

        // The audit row says who and whom, never what.
        var staffId = await _o.WithDbAsync(db => db.AuditLogs.Where(a => a.Entity == PrivacyService.AuditEntity && a.EntityId == person.Id.ToString()).Select(a => a.UserId).SingleAsync(Ct));
        Assert.NotNull(staffId);
        var row = await _o.WithDbAsync(db => db.AuditLogs.AsNoTracking().SingleAsync(a => a.Entity == PrivacyService.AuditEntity && a.EntityId == person.Id.ToString(), Ct));
        Assert.Equal(AuditAction.Viewed, row.Action);
        Assert.Contains("export", row.NewValue, StringComparison.Ordinal);
        foreach (var secret in new[] { person.Full, person.Email, person.Name, person.TicketText, person.OrderNumber, "Privacy Road" })
        {
            Assert.DoesNotContain(secret, row.NewValue ?? string.Empty, StringComparison.Ordinal);
            Assert.DoesNotContain(secret, row.OldValue ?? string.Empty, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task A_staff_member_can_export_only_a_few_customers_per_hour()
    {
        var admin = await H.AdminAsync();
        var person = await SeedAsync(admin, cancelOrder: false);
        var staff = await StaffAsync("customers.privacy.export");

        for (var i = 0; i < PrivacyService.ExportsPerHour; i++)
        {
            Assert.Equal(HttpStatusCode.OK, (await H.SendAsync(HttpMethod.Post, Url(person.Id, "privacy/export"), new { }, staff)).StatusCode);
        }

        var limited = await H.SendAsync(HttpMethod.Post, Url(person.Id, "privacy/export"), new { }, staff);
        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
        await StoreHelper.ExpectCodeAsync(limited, "PRIVACY.EXPORT_LIMIT");
    }

    // ---- anonymise -------------------------------------------------------------------------------------------------

    private Task<HttpResponseMessage> AnonymiseAsync(string bearer, Guid id, string? reason = "Customer asked by e-mail", string? confirm = "id") =>
        H.SendAsync(HttpMethod.Post, Url(id, "privacy/anonymise"), new { reason, confirm = confirm == "id" ? id.ToString() : confirm }, bearer);

    [Fact]
    public async Task Anonymising_needs_a_reason_and_the_customer_id_and_refuses_open_work_and_staff()
    {
        var admin = await H.AdminAsync();
        var person = await SeedAsync(admin, cancelOrder: false);

        await StoreHelper.ExpectCodeAsync(await AnonymiseAsync(admin, person.Id, reason: " "), "PRIVACY.REASON_REQUIRED");
        await StoreHelper.ExpectCodeAsync(await AnonymiseAsync(admin, person.Id, confirm: null), "PRIVACY.CONFIRM_MISMATCH");
        await StoreHelper.ExpectCodeAsync(await AnonymiseAsync(admin, person.Id, confirm: Guid.NewGuid().ToString()), "PRIVACY.CONFIRM_MISMATCH");
        var open = await AnonymiseAsync(admin, person.Id);
        Assert.Equal(HttpStatusCode.Conflict, open.StatusCode);
        await StoreHelper.ExpectCodeAsync(open, "PRIVACY.OPEN_ORDERS");

        var staffId = await _o.WithDbAsync(db => db.Users.Select(u => u.Id).FirstAsync(Ct));
        var staff = await AnonymiseAsync(admin, staffId);
        Assert.Equal(HttpStatusCode.Conflict, staff.StatusCode);
        await StoreHelper.ExpectCodeAsync(staff, "PRIVACY.STAFF_ACCOUNT");
        Assert.Equal(HttpStatusCode.NotFound, (await AnonymiseAsync(admin, Guid.CreateVersion7())).StatusCode);

        // Nothing changed.
        var customer = await _o.WithDbAsync(db => db.Customers.AsNoTracking().SingleAsync(c => c.Id == person.Id, Ct));
        Assert.Equal(person.Full, customer.Mobile);
        Assert.Null(customer.AnonymisedAt);
    }

    [Fact]
    public async Task Anonymising_replaces_the_personal_data_keeps_the_books_and_lets_the_number_register_again()
    {
        var admin = await H.AdminAsync();
        var person = await SeedAsync(admin, cancelOrder: true, invoice: true);
        var other = await SeedAsync(admin, cancelOrder: true);
        var orderBefore = await _o.WithDbAsync(db => db.Orders.AsNoTracking().SingleAsync(o => o.Id == person.OrderId, Ct));
        var invoiceBefore = await _o.WithDbAsync(db => db.Invoices.AsNoTracking().SingleAsync(i => i.OrderId == person.OrderId, Ct));
        var auditText = await _o.WithDbAsync(db => db.AuditLogs.Where(a => a.Entity == "Customer" && a.EntityId == person.Id.ToString()).Select(a => a.NewValue).ToListAsync(Ct));
        Assert.Contains(auditText, v => v is not null && v.Contains(person.Email, StringComparison.Ordinal));   // the trail held the e-mail before

        var response = await AnonymiseAsync(admin, person.Id);
        var result = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct)).RootElement;

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.False(result.GetProperty("alreadyAnonymised").GetBoolean());
        Assert.Equal(1, result.GetProperty("scrubbed").GetProperty("orders").GetInt32());
        Assert.Equal(1, result.GetProperty("scrubbed").GetProperty("tickets").GetInt32());
        Assert.Equal(1, result.GetProperty("scrubbed").GetProperty("contactMessages").GetInt32());
        Assert.Equal(1, result.GetProperty("scrubbed").GetProperty("addressesRemoved").GetInt32());
        Assert.Equal(1, result.GetProperty("scrubbed").GetProperty("wishlistRemoved").GetInt32());
        Assert.True(result.GetProperty("scrubbed").GetProperty("sessionsRemoved").GetInt32() >= 1);
        Assert.True(result.GetProperty("kept").GetArrayLength() >= 5);

        await _o.WithDbAsync(async db =>
        {
            var customer = await db.Customers.AsNoTracking().SingleAsync(c => c.Id == person.Id, Ct);
            Assert.True(customer.IsAnonymised);
            Assert.False(customer.IsActive);
            Assert.Null(customer.Email);
            Assert.Equal(PrivacyPlaceholders.Name, customer.FullName);
            Assert.NotEqual(person.Full, customer.Mobile);
            Assert.True(PrivacyPlaceholders.IsPlaceholderMobile(customer.Mobile));

            // The order stays with its totals and status; the person is gone.
            var order = await db.Orders.AsNoTracking().SingleAsync(o => o.Id == person.OrderId, Ct);
            Assert.Equal(orderBefore.Number, order.Number);
            Assert.Equal(orderBefore.GrandTotal, order.GrandTotal);
            Assert.Equal(orderBefore.Subtotal, order.Subtotal);
            Assert.Equal(orderBefore.Status, order.Status);
            Assert.Equal(orderBefore.ShipDistrict, order.ShipDistrict);
            Assert.Equal(person.Id, order.CustomerId);
            Assert.Equal(PrivacyPlaceholders.Name, order.CustomerName);
            Assert.Null(order.CustomerEmail);
            Assert.NotEqual(person.Full, order.CustomerMobile);
            Assert.Equal(PrivacyPlaceholders.Name, order.ShipName);
            Assert.Equal(PrivacyPlaceholders.Address, order.ShipAddressLine);
            Assert.NotEqual(orderBefore.ShipPhone, order.ShipPhone);
            Assert.Equal(1, await db.OrderItems.CountAsync(i => i.OrderId == person.OrderId, Ct));
            Assert.DoesNotContain(await db.OrderNotes.Where(n => n.OrderId == person.OrderId).Select(n => n.Text).ToListAsync(Ct), t => t.Contains("Gift", StringComparison.Ordinal) || t.Contains("called", StringComparison.Ordinal));

            // The invoice is a tax record: it keeps the buyer exactly as issued.
            var invoice = await db.Invoices.AsNoTracking().SingleAsync(i => i.OrderId == person.OrderId, Ct);
            Assert.Equal(invoiceBefore.CustomerName, invoice.CustomerName);
            Assert.Equal(invoiceBefore.BillingAddress, invoice.BillingAddress);
            Assert.Equal(invoiceBefore.GrandTotal, invoice.GrandTotal);

            // Free text and contact details of tickets and contact messages.
            var ticket = await db.Tickets.AsNoTracking().SingleAsync(t => t.Id == person.TicketId, Ct);
            Assert.Equal(PrivacyPlaceholders.Text, ticket.Subject);
            Assert.NotEqual(person.Full, ticket.CustomerMobile);
            Assert.Null(ticket.CustomerEmail);
            Assert.All(await db.TicketMessages.Where(m => m.TicketId == person.TicketId).ToListAsync(Ct), m => Assert.Equal(PrivacyPlaceholders.Text, m.Text));
            Assert.Equal(0, await db.ContactMessages.CountAsync(m => m.Message.Contains(person.ContactText) || m.Email == person.Email, Ct));
            Assert.Equal(0, await db.NotificationLogs.CountAsync(n => n.Recipient == person.Full || n.Recipient == person.Email || n.Recipient == person.Mobile, Ct));

            // Removed outright.
            Assert.Equal(0, await db.Addresses.IgnoreQueryFilters().CountAsync(a => a.CustomerId == person.Id, Ct));
            Assert.Equal(0, await db.WishlistItems.CountAsync(w => w.CustomerId == person.Id, Ct));
            Assert.Equal(0, await db.RefreshTokens.CountAsync(t => t.SubjectId == person.Id, Ct));
            Assert.Equal(0, await db.SignInHistory.CountAsync(h => h.SubjectId == person.Id && h.IpAddress != null, Ct));

            // The consent history stays; the audit trail no longer holds the e-mail or the name.
            Assert.True(await db.ConsentLogs.CountAsync(c => c.CustomerId == person.Id, Ct) >= 1);
            var trail = await db.AuditLogs.Where(a => (a.Entity == "Customer" || a.Entity == "Order" || a.Entity == "Ticket" || a.Entity == "Address") && a.Action != AuditAction.Viewed).ToListAsync(Ct);
            Assert.DoesNotContain(trail, a => (a.NewValue ?? string.Empty).Contains(person.Email, StringComparison.Ordinal) || (a.OldValue ?? string.Empty).Contains(person.Email, StringComparison.Ordinal) || (a.NewValue ?? string.Empty).Contains(person.Name, StringComparison.Ordinal));

            // The action itself is audited with the reason and without personal data.
            var audit = await db.AuditLogs.AsNoTracking().SingleAsync(a => a.Entity == PrivacyService.AuditEntity && a.EntityId == person.Id.ToString(), Ct);
            Assert.NotNull(audit.UserId);
            Assert.Equal(AuditAction.Updated, audit.Action);
            Assert.Contains("Customer asked by e-mail", audit.NewValue, StringComparison.Ordinal);
            Assert.DoesNotContain(person.Email, audit.NewValue ?? string.Empty, StringComparison.Ordinal);

            // Another customer is untouched.
            var untouched = await db.Customers.AsNoTracking().SingleAsync(c => c.Id == other.Id, Ct);
            Assert.False(untouched.IsAnonymised);
            Assert.Equal(other.Full, untouched.Mobile);
            Assert.Equal("Nadia Rahman", (await db.Orders.AsNoTracking().SingleAsync(o => o.Id == other.OrderId, Ct)).CustomerName);   // the name given at checkout
            return 0;
        });

        // Sign-in as the old customer is impossible: the session is gone, and the number now starts a new customer.
        Assert.Equal(HttpStatusCode.Unauthorized, (await H.Api.PostAsync("/api/v1/auth/refresh", new { refreshToken = person.Refresh })).StatusCode);
        var (_, freshAccess) = await SignInAsync(person.Mobile);
        var profile = await H.JsonAsync(HttpMethod.Get, "/api/v1/me", HttpStatusCode.OK, bearer: freshAccess);
        Assert.NotEqual(person.Id, profile.GetProperty("id").GetGuid());
        Assert.Equal(person.Full, profile.GetProperty("mobile").GetString());
        Assert.Null(profile.GetProperty("fullName").GetString());
        var orders = await H.JsonAsync(HttpMethod.Get, "/api/v1/me/orders", HttpStatusCode.OK, bearer: freshAccess);
        Assert.Equal(0, orders.GetProperty("total").GetInt32());

        // The export of the anonymised customer shows only placeholders.
        var staff = await StaffAsync("customers.privacy.export");
        var export = await (await H.SendAsync(HttpMethod.Post, Url(person.Id, "privacy/export"), new { }, staff)).Content.ReadAsStringAsync(Ct);
        Assert.DoesNotContain(person.Email, export, StringComparison.Ordinal);
        Assert.DoesNotContain("Privacy Road", export, StringComparison.Ordinal);
        Assert.Contains("anonymisedAt", export, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Anonymising_twice_is_safe_and_changes_nothing_the_second_time()
    {
        var admin = await H.AdminAsync();
        var person = await SeedAsync(admin, cancelOrder: true);

        var first = JsonDocument.Parse(await (await AnonymiseAsync(admin, person.Id)).Content.ReadAsStringAsync(Ct)).RootElement;
        var stamp = await _o.WithDbAsync(db => db.Customers.AsNoTracking().Where(c => c.Id == person.Id).Select(c => c.AnonymisedAt).SingleAsync(Ct));
        var auditRows = await _o.WithDbAsync(db => db.AuditLogs.CountAsync(a => a.Entity == PrivacyService.AuditEntity && a.EntityId == person.Id.ToString(), Ct));
        var secondResponse = await AnonymiseAsync(admin, person.Id, reason: "A second request");
        var second = JsonDocument.Parse(await secondResponse.Content.ReadAsStringAsync(Ct)).RootElement;

        Assert.False(first.GetProperty("alreadyAnonymised").GetBoolean());
        Assert.Equal(HttpStatusCode.OK, secondResponse.StatusCode);
        Assert.True(second.GetProperty("alreadyAnonymised").GetBoolean());
        Assert.Equal(0, second.GetProperty("scrubbed").GetProperty("orders").GetInt32());
        Assert.Equal(stamp, await _o.WithDbAsync(db => db.Customers.AsNoTracking().Where(c => c.Id == person.Id).Select(c => c.AnonymisedAt).SingleAsync(Ct)));
        Assert.Equal(auditRows, await _o.WithDbAsync(db => db.AuditLogs.CountAsync(a => a.Entity == PrivacyService.AuditEntity && a.EntityId == person.Id.ToString(), Ct)));
    }
}
