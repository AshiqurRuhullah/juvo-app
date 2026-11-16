using System.Net;
using System.Text.Json;
using Juvo.Api.IntegrationTests.Storefront;
using Juvo.Api.IntegrationTests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Juvo.Api.IntegrationTests.Operations;

public class SupportTicketTests(OpsFactory factory) : IClassFixture<OpsFactory>
{
    private readonly SupportHelper _s = new(factory);

    private OpsHelper O => _s.O;

    private StoreHelper H => _s.H;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<(string Admin, Guid Ho, string Staff)> SetupAsync()
    {
        var admin = await H.AdminAsync();
        var ho = await O.HoAsync();
        return (admin, ho, await _s.StaffAsync(admin, ho, SupportHelper.AllSupport));
    }

    private static string[] Statuses(JsonElement ticket) => [.. ticket.GetProperty("history").EnumerateArray().Select(h => h.GetProperty("toStatus").GetString()!)];

    // ---- opening a ticket ------------------------------------------------------------------------------------

    [Fact]
    public async Task A_guest_writes_through_the_contact_form_and_staff_see_it_with_the_first_message()
    {
        var (_, _, staff) = await SetupAsync();
        var mobile = StoreHelper.NewMobile();

        var number = await _s.ContactAsync(mobile, email: "nadia@example.com", subject: "Parcel late", message: "It was due yesterday.", category: "delivery");
        var ticket = await _s.FindAsync(staff, number);

        Assert.Matches(@"^TK-\d{4}-\d{6}$", number);
        Assert.Equal("Open", ticket.GetProperty("status").GetString());
        Assert.Equal("Normal", ticket.GetProperty("priority").GetString());
        Assert.Equal("Web", ticket.GetProperty("channel").GetString());
        Assert.Equal("delivery", ticket.GetProperty("category").GetString());
        Assert.Equal(SupportHelper.Normalised(mobile), ticket.GetProperty("customerMobile").GetString());
        Assert.Equal("nadia@example.com", ticket.GetProperty("customerEmail").GetString());
        Assert.Equal(JsonValueKind.Null, ticket.GetProperty("customerId").ValueKind); // a guest is never linked to an account by a typed number
        Assert.Equal(JsonValueKind.Null, ticket.GetProperty("branchId").ValueKind);
        var first = Assert.Single(ticket.GetProperty("messages").EnumerateArray());
        Assert.Equal("Customer", first.GetProperty("kind").GetString());
        Assert.Equal("It was due yesterday.", first.GetProperty("text").GetString());
        Assert.Equal(["Open"], Statuses(ticket));
    }

    [Theory]
    [InlineData("abc", "Name", "Subject", "Message")]
    [InlineData("01712345678", "", "Subject", "Message")]
    [InlineData("01712345678", "Name", "", "Message")]
    [InlineData("01712345678", "Name", "Subject", "   ")]
    public async Task The_contact_form_refuses_missing_or_bad_fields(string mobile, string name, string subject, string message)
    {
        var response = await _s.TryContactAsync(mobile, name, subject: subject, message: message);

        await StoreHelper.ExpectCodeAsync(response, "GENERAL.VALIDATION");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task The_contact_form_refuses_a_bad_email_and_an_unknown_category()
    {
        await StoreHelper.ExpectCodeAsync(await _s.TryContactAsync(email: "not-an-email"), "GENERAL.VALIDATION");
        await StoreHelper.ExpectCodeAsync(await _s.TryContactAsync(category: "complaint"), "GENERAL.VALIDATION");
    }

    [Fact]
    public async Task Ticket_numbers_follow_each_other_without_gaps_even_when_tickets_are_opened_at_the_same_time()
    {
        var numbers = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => _s.ContactAsync()));

        Assert.Equal(6, numbers.Distinct().Count());
        var serials = numbers.Select(n => int.Parse(n[^6..], System.Globalization.CultureInfo.InvariantCulture)).Order().ToArray();
        Assert.Equal(Enumerable.Range(serials[0], 6), serials);
        Assert.All(numbers, n => Assert.Equal(numbers[0][..8], n[..8])); // one month, one counter
    }

    [Fact]
    public async Task A_signed_in_customer_links_only_their_own_order_and_the_ticket_takes_the_branch_of_the_order()
    {
        var (admin, ho, staff) = await SetupAsync();
        var (orderNumber, mobile, _) = await _s.OrderAsync(admin);
        var customer = await H.CustomerTokenAsync(mobile);
        var stranger = await H.CustomerTokenAsync();

        var mine = await _s.MyCreateAsync(customer, "Wrong colour", "It is blue, I wanted red.", "order", orderNumber);
        Assert.Equal(orderNumber, mine.GetProperty("orderNumber").GetString());
        Assert.Equal("Open", mine.GetProperty("status").GetString());

        await StoreHelper.ExpectCodeAsync(await _s.TryMyCreateAsync(stranger, orderNumber), "TICKET.ORDER_MISMATCH");
        await StoreHelper.ExpectCodeAsync(await _s.TryMyCreateAsync(stranger, "JV-HO-0001-999999"), "TICKET.ORDER_MISMATCH"); // no hint whether the order exists

        var seen = await _s.FindAsync(staff, mine.GetProperty("number").GetString()!);
        Assert.Equal(ho, seen.GetProperty("branchId").GetGuid());
        Assert.Equal("HO", seen.GetProperty("branchCode").GetString());
        Assert.Equal(orderNumber, seen.GetProperty("orderNumber").GetString());
        Assert.Equal("Web", seen.GetProperty("channel").GetString());
    }

    [Fact]
    public async Task A_guest_links_an_order_only_with_the_mobile_that_placed_it()
    {
        var (admin, ho, staff) = await SetupAsync();
        var (orderNumber, mobile, _) = await _s.OrderAsync(admin);

        await StoreHelper.ExpectCodeAsync(await _s.TryContactAsync(StoreHelper.NewMobile(), orderNumber: orderNumber), "TICKET.ORDER_MISMATCH");
        await StoreHelper.ExpectCodeAsync(await _s.TryContactAsync(mobile, orderNumber: "JV-HO-0001-999999"), "TICKET.ORDER_MISMATCH");

        var ok = await TestApi.ExpectAsync(await _s.TryContactAsync(mobile, orderNumber: orderNumber), HttpStatusCode.Created);
        var ticket = await _s.FindAsync(staff, ok.GetProperty("number").GetString()!);
        Assert.Equal(orderNumber, ticket.GetProperty("orderNumber").GetString());
        Assert.Equal(ho, ticket.GetProperty("branchId").GetGuid());
    }

    [Fact]
    public async Task Staff_open_a_ticket_for_a_phone_customer_and_the_customer_sees_it_in_the_panel()
    {
        var (admin, ho, staff) = await SetupAsync();
        var (orderNumber, mobile, _) = await _s.OrderAsync(admin);
        var customer = await H.CustomerTokenAsync(mobile);

        var ticket = await _s.StaffCreateAsync(staff, mobile, orderNumber, "Call about the size", "Wants to exchange.", "return", "High");

        Assert.Equal("Phone", ticket.GetProperty("channel").GetString());
        Assert.Equal("High", ticket.GetProperty("priority").GetString());
        Assert.Equal(ho, ticket.GetProperty("branchId").GetGuid());
        Assert.NotEqual(JsonValueKind.Null, ticket.GetProperty("customerId").ValueKind); // staff may look the customer up by mobile
        var mine = await H.JsonAsync(HttpMethod.Get, "/api/v1/me/tickets", HttpStatusCode.OK, bearer: customer);
        Assert.Contains(mine.GetProperty("items").EnumerateArray(), t => t.GetProperty("number").GetString() == ticket.GetProperty("number").GetString());
    }

    [Fact]
    public async Task Staff_creation_checks_the_order_the_mobile_the_assignee_and_the_permission()
    {
        var (admin, ho, staff) = await SetupAsync();
        var (orderNumber, mobile, _) = await _s.OrderAsync(admin);

        await StoreHelper.ExpectCodeAsync(await _s.TryStaffCreateAsync(staff, new { mobile, subject = "x", message = "y", orderNumber = "JV-HO-0001-999999" }), "TICKET.ORDER_NOT_FOUND");
        await StoreHelper.ExpectCodeAsync(await _s.TryStaffCreateAsync(staff, new { mobile = StoreHelper.NewMobile(), subject = "x", message = "y", orderNumber }), "TICKET.ORDER_MISMATCH");
        await StoreHelper.ExpectCodeAsync(await _s.TryStaffCreateAsync(staff, new { mobile = "abc", subject = "x", message = "y" }), "GENERAL.VALIDATION");
        await StoreHelper.ExpectCodeAsync(await _s.TryStaffCreateAsync(staff, new { mobile, subject = "x", message = "y", assigneeId = Guid.NewGuid() }), "TICKET.ASSIGNEE_NOT_FOUND");

        var viewOnly = await _s.StaffAsync(admin, ho, "support.ticket.view");
        Assert.Equal(HttpStatusCode.Forbidden, (await _s.TryStaffCreateAsync(viewOnly, new { mobile, subject = "x", message = "y" })).StatusCode);

        // Assigning at creation needs the assign permission and an assignee who can work on the ticket.
        var worker = await WorkerAsync(admin, ho);
        var created = await _s.StaffCreateAsync(staff, mobile, assignee: worker.Id);
        Assert.Equal(worker.Id, created.GetProperty("assigneeId").GetGuid());
        var creatorOnly = await _s.StaffAsync(admin, ho, "support.ticket.view", "support.ticket.create");
        Assert.Equal(HttpStatusCode.Forbidden, (await _s.TryStaffCreateAsync(creatorOnly, new { mobile, subject = "x", message = "y", assigneeId = worker.Id })).StatusCode);
    }

    private async Task<(Guid Id, string Token)> WorkerAsync(string admin, Guid branch)
    {
        var role = await H.Api.CreateRoleAsync(admin, StoreHelper.Unique("Role"), ["support.ticket.view", "support.ticket.reply"]);
        var user = await H.Api.CreateUserAsync(admin, $"{StoreHelper.Unique("wk")}@example.com", "Rina Worker", false, (branch, role));
        var token = (await H.Api.LoginAsync(user.Email, user.Password)).Access;
        return (user.Id, token);
    }

    // ---- the conversation ----------------------------------------------------------------------------------------

    [Fact]
    public async Task A_ticket_goes_from_open_to_closed_with_a_history_row_for_every_move()
    {
        var (admin, _, staff) = await SetupAsync();
        var mobile = StoreHelper.NewMobile();
        var customer = await H.CustomerTokenAsync(mobile);
        var created = await _s.MyCreateAsync(customer);
        var id = created.GetProperty("id").GetGuid();

        var replied = await _s.ReplyAsync(staff, id, "We are looking into it.");
        Assert.Equal("InProgress", replied.GetProperty("status").GetString()); // the first public reply picks the ticket up
        Assert.NotEqual(JsonValueKind.Null, replied.GetProperty("firstResponseAt").ValueKind);

        var waiting = await _s.ReplyAsync(staff, id, "Could you send a photo?", setStatus: "Waiting");
        Assert.Equal("Waiting", waiting.GetProperty("status").GetString());

        var answered = await _s.MyActAsync(customer, id, "reply", new { text = "Here is the photo." });
        Assert.Equal("InProgress", answered.GetProperty("status").GetString()); // the customer's answer ends the waiting

        var resolved = await _s.ActAsync(staff, id, "resolve", new { reason = "Exchange arranged" });
        Assert.Equal("Resolved", resolved.GetProperty("status").GetString());
        Assert.NotEqual(JsonValueKind.Null, resolved.GetProperty("resolvedAt").ValueKind);

        var again = await _s.MyActAsync(customer, id, "reply", new { text = "It is still wrong." });
        Assert.Equal("Open", again.GetProperty("status").GetString()); // a reply to a resolved ticket reopens it

        var closed = await _s.ActAsync(staff, id, "close", new { reason = "Done" });
        Assert.Equal("Closed", closed.GetProperty("status").GetString());
        Assert.Equal(["Open", "InProgress", "Waiting", "InProgress", "Resolved", "Open", "Closed"], Statuses(closed));
        Assert.Equal(1, closed.GetProperty("reopenCount").GetInt32());
        var last = closed.GetProperty("history").EnumerateArray().Last();
        Assert.Equal("Done", last.GetProperty("reason").GetString());
        Assert.False(string.IsNullOrEmpty(last.GetProperty("actorName").GetString()));
    }

    [Fact]
    public async Task Moves_the_state_machine_does_not_allow_are_refused_with_a_conflict()
    {
        var (_, _, staff) = await SetupAsync();
        var id = (await _s.GuestTicketAsync(staff)).GetProperty("id").GetGuid();

        await StoreHelper.ExpectCodeAsync(await _s.TryActAsync(staff, id, "status", new { status = "Open" }), "TICKET.INVALID_TRANSITION"); // same status
        await StoreHelper.ExpectCodeAsync(await _s.TryActAsync(staff, id, "reopen"), "TICKET.INVALID_TRANSITION"); // Open -> Open
        await _s.ActAsync(staff, id, "close");
        await StoreHelper.ExpectCodeAsync(await _s.TryActAsync(staff, id, "resolve"), "TICKET.INVALID_TRANSITION"); // Closed -> Resolved
        await StoreHelper.ExpectCodeAsync(await _s.TryActAsync(staff, id, "status", new { status = "InProgress" }), "TICKET.INVALID_TRANSITION");
        Assert.Equal(HttpStatusCode.BadRequest, (await _s.TryActAsync(staff, id, "status", new { status = 99 })).StatusCode);
        var reopened = await _s.ActAsync(staff, id, "reopen");
        Assert.Equal("Open", reopened.GetProperty("status").GetString());
    }

    [Fact]
    public async Task A_closed_ticket_takes_no_reply_from_the_customer_or_a_public_reply_from_staff_but_still_takes_notes()
    {
        var (_, _, staff) = await SetupAsync();
        var customer = await H.CustomerTokenAsync();
        var id = (await _s.MyCreateAsync(customer)).GetProperty("id").GetGuid();
        await _s.ActAsync(staff, id, "close");

        await StoreHelper.ExpectCodeAsync(await _s.TryMyActAsync(customer, id, "reply", new { text = "Hello?" }), "TICKET.CLOSED");
        await StoreHelper.ExpectCodeAsync(await _s.TryActAsync(staff, id, "reply", new { text = "Hello", @internal = false }), "TICKET.CLOSED");
        var noted = await _s.ReplyAsync(staff, id, "Closed by mistake?", isInternal: true);
        Assert.Equal("Closed", noted.GetProperty("status").GetString());
    }

    [Fact]
    public async Task A_customer_can_close_and_reopen_their_own_ticket_but_cannot_reopen_one_that_is_still_open()
    {
        var customer = await H.CustomerTokenAsync();
        var id = (await _s.MyCreateAsync(customer)).GetProperty("id").GetGuid();

        await StoreHelper.ExpectCodeAsync(await _s.TryMyActAsync(customer, id, "reopen"), "TICKET.INVALID_TRANSITION");
        var closed = await _s.MyActAsync(customer, id, "close");
        Assert.Equal("Closed", closed.GetProperty("status").GetString());
        Assert.False(closed.GetProperty("canReply").GetBoolean());
        Assert.True(closed.GetProperty("canReopen").GetBoolean());
        await StoreHelper.ExpectCodeAsync(await _s.TryMyActAsync(customer, id, "close"), "TICKET.INVALID_TRANSITION");
        var reopened = await _s.MyActAsync(customer, id, "reopen");
        Assert.Equal("Open", reopened.GetProperty("status").GetString());
        Assert.True(reopened.GetProperty("canReply").GetBoolean());
    }

    // ---- what a customer may see ---------------------------------------------------------------------------------

    [Fact]
    public async Task A_customer_never_sees_internal_notes_nor_the_name_of_a_staff_member()
    {
        var (_, _, staff) = await SetupAsync();
        var customer = await H.CustomerTokenAsync();
        var id = (await _s.MyCreateAsync(customer)).GetProperty("id").GetGuid();
        await _s.ReplyAsync(staff, id, "SECRET-NOTE: customer is a repeat returner", isInternal: true);
        await _s.ReplyAsync(staff, id, "We will exchange it.");

        var staffView = await _s.GetAsync(staff, id);
        var theirs = await _s.MyGetAsync(customer, id);

        Assert.Contains(staffView.GetProperty("messages").EnumerateArray(), m => m.GetProperty("kind").GetString() == "Internal");
        var raw = theirs.GetRawText();
        Assert.DoesNotContain("SECRET-NOTE", raw, StringComparison.Ordinal);
        Assert.DoesNotContain("Internal", raw, StringComparison.Ordinal);
        var kinds = theirs.GetProperty("messages").EnumerateArray().Select(m => m.GetProperty("kind").GetString()).ToList();
        Assert.Equal(["Customer", "Staff"], kinds);
        var staffName = staffView.GetProperty("messages").EnumerateArray().First(m => m.GetProperty("kind").GetString() == "Staff").GetProperty("authorName").GetString();
        Assert.False(string.IsNullOrWhiteSpace(staffName));
        Assert.DoesNotContain(staffName!, raw, StringComparison.Ordinal);
        Assert.Contains("Juvo BD Support", raw, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Another_customers_ticket_and_a_guest_ticket_look_like_not_found_to_every_customer_call()
    {
        var (_, _, staff) = await SetupAsync();
        var owner = await H.CustomerTokenAsync();
        var stranger = await H.CustomerTokenAsync();
        var id = (await _s.MyCreateAsync(owner)).GetProperty("id").GetGuid();
        var guest = (await _s.GuestTicketAsync(staff)).GetProperty("id").GetGuid();

        foreach (var target in new[] { id, guest })
        {
            await StoreHelper.ExpectCodeAsync(await H.SendAsync(HttpMethod.Get, $"/api/v1/me/tickets/{target}", bearer: stranger), "TICKET.NOT_FOUND");
            await StoreHelper.ExpectCodeAsync(await _s.TryMyActAsync(stranger, target, "reply", new { text = "hi" }), "TICKET.NOT_FOUND");
            await StoreHelper.ExpectCodeAsync(await _s.TryMyActAsync(stranger, target, "close"), "TICKET.NOT_FOUND");
            await StoreHelper.ExpectCodeAsync(await _s.TryMyActAsync(stranger, target, "reopen"), "TICKET.NOT_FOUND");
        }

        Assert.Equal(HttpStatusCode.NotFound, (await H.SendAsync(HttpMethod.Get, $"/api/v1/me/tickets/{guest}", bearer: owner)).StatusCode);
        var list = await H.JsonAsync(HttpMethod.Get, "/api/v1/me/tickets", HttpStatusCode.OK, bearer: stranger);
        Assert.Equal(0, list.GetProperty("total").GetInt32());
        var ownerList = await H.JsonAsync(HttpMethod.Get, "/api/v1/me/tickets", HttpStatusCode.OK, bearer: owner);
        Assert.Equal(1, ownerList.GetProperty("total").GetInt32());
    }

    [Fact]
    public async Task The_customer_endpoints_need_a_customer_token_and_the_staff_endpoints_a_staff_token()
    {
        var (admin, _, staff) = await SetupAsync();
        var customer = await H.CustomerTokenAsync();

        Assert.Equal(HttpStatusCode.Unauthorized, (await H.SendAsync(HttpMethod.Get, "/api/v1/me/tickets")).StatusCode);
        Assert.Contains((await H.SendAsync(HttpMethod.Get, "/api/v1/me/tickets", bearer: staff)).StatusCode, new[] { HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden });
        Assert.Equal(HttpStatusCode.Unauthorized, (await H.SendAsync(HttpMethod.Get, SupportHelper.TicketsUrl)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await H.SendAsync(HttpMethod.Get, SupportHelper.TicketsUrl, bearer: customer)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await H.SendAsync(HttpMethod.Get, "/api/v1/support/report", bearer: customer)).StatusCode);
        Assert.NotNull(admin);
    }

    // ---- limits ---------------------------------------------------------------------------------------------------

    [Fact]
    public async Task One_mobile_number_can_open_only_five_tickets_an_hour_and_the_sixth_is_rate_limited()
    {
        var mobile = StoreHelper.NewMobile();
        for (var i = 0; i < 5; i++)
        {
            await _s.ContactAsync(mobile);
        }

        var sixth = await _s.TryContactAsync(mobile);
        await StoreHelper.ExpectCodeAsync(sixth, "TICKET.RATE_LIMITED");
        Assert.Equal(HttpStatusCode.TooManyRequests, sixth.StatusCode);
        await _s.ContactAsync(StoreHelper.NewMobile()); // another number is not affected

        var customer = await H.CustomerTokenAsync(mobile);
        await StoreHelper.ExpectCodeAsync(await _s.TryMyCreateAsync(customer), "TICKET.RATE_LIMITED"); // the panel has the same limit
    }

    [Fact]
    public async Task The_hourly_limit_is_a_setting_and_staff_creation_is_not_limited()
    {
        var (_, _, staff) = await SetupAsync();
        var mobile = StoreHelper.NewMobile();
        await _s.SetPlatformSettingAsync("Support.MaxTicketsPerMobilePerHour", "2");
        try
        {
            await _s.ContactAsync(mobile);
            await _s.ContactAsync(mobile);
            await StoreHelper.ExpectCodeAsync(await _s.TryContactAsync(mobile), "TICKET.RATE_LIMITED");
            await _s.StaffCreateAsync(staff, mobile); // a person on the phone is not the abuse the limit is for
        }
        finally
        {
            await _s.SetPlatformSettingAsync("Support.MaxTicketsPerMobilePerHour", null);
        }
    }

    // ---- attachments ----------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_customer_attaches_an_uploaded_picture_and_staff_can_open_it()
    {
        var (_, _, staff) = await SetupAsync();
        var customer = await H.CustomerTokenAsync();
        var path = await _s.UploadedPathAsync("/api/v1/me/tickets/uploads", customer);

        var created = await _s.MyCreateAsync(customer, paths: [path]);

        var message = Assert.Single(created.GetProperty("messages").EnumerateArray());
        var file = Assert.Single(message.GetProperty("attachments").EnumerateArray());
        Assert.Equal("image/png", file.GetProperty("contentType").GetString());
        Assert.EndsWith(".png", file.GetProperty("fileName").GetString(), StringComparison.Ordinal);
        var seen = await _s.GetAsync(staff, created.GetProperty("id").GetGuid());
        Assert.Single(seen.GetProperty("messages")[0].GetProperty("attachments").EnumerateArray());
        var download = await H.Api.Client.GetAsync(file.GetProperty("url").GetString(), Ct);
        Assert.Equal(HttpStatusCode.OK, download.StatusCode);
    }

    [Fact]
    public async Task Attachments_are_judged_by_their_content_and_a_path_that_is_not_yours_is_refused()
    {
        var (_, _, staff) = await SetupAsync();
        var customer = await H.CustomerTokenAsync();
        var other = await H.CustomerTokenAsync();

        var text = await _s.UploadAsync("/api/v1/me/tickets/uploads", customer, System.Text.Encoding.UTF8.GetBytes("hello, not a picture"), "evil.png");
        Assert.Equal(HttpStatusCode.BadRequest, text.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await _s.UploadAsync("/api/v1/me/tickets/uploads", customer, [])).StatusCode);

        var theirs = await _s.UploadedPathAsync("/api/v1/me/tickets/uploads", other);
        var staffs = await _s.UploadedPathAsync("/api/v1/support/tickets/uploads", staff);
        await StoreHelper.ExpectCodeAsync(await _s.TryMyCreateAsync(customer, paths: [theirs]), "TICKET.ATTACHMENT_INVALID");
        await StoreHelper.ExpectCodeAsync(await _s.TryMyCreateAsync(customer, paths: [staffs]), "TICKET.ATTACHMENT_INVALID");
        await StoreHelper.ExpectCodeAsync(await _s.TryMyCreateAsync(customer, paths: ["tickets/../secrets.png"]), "TICKET.ATTACHMENT_INVALID");
        await StoreHelper.ExpectCodeAsync(await _s.TryMyCreateAsync(customer, paths: ["expenses/2611/receipt.png"]), "TICKET.ATTACHMENT_INVALID");

        var many = new List<string>();
        for (var i = 0; i < 6; i++)
        {
            many.Add(await _s.UploadedPathAsync("/api/v1/me/tickets/uploads", customer));
        }

        await StoreHelper.ExpectCodeAsync(await _s.TryMyCreateAsync(customer, paths: [.. many]), "TICKET.ATTACHMENT_INVALID"); // at most five files
        Assert.Equal(HttpStatusCode.Forbidden, (await _s.UploadAsync("/api/v1/me/tickets/uploads", staff, SupportHelper.PngBytes())).StatusCode);
    }

    [Fact]
    public async Task Staff_attach_a_file_to_a_reply_and_the_customer_can_open_it()
    {
        var (_, _, staff) = await SetupAsync();
        var customer = await H.CustomerTokenAsync();
        var id = (await _s.MyCreateAsync(customer)).GetProperty("id").GetGuid();
        var path = await _s.UploadedPathAsync("/api/v1/support/tickets/uploads", staff);

        await _s.ActAsync(staff, id, "reply", new { text = "Return label attached.", attachmentPaths = new[] { path } });

        var mine = await _s.MyGetAsync(customer, id);
        var reply = mine.GetProperty("messages").EnumerateArray().Single(m => m.GetProperty("kind").GetString() == "Staff");
        Assert.Single(reply.GetProperty("attachments").EnumerateArray());
    }

    // ---- assignment, priority, filters ----------------------------------------------------------------------------

    [Fact]
    public async Task Assigning_and_changing_the_priority_write_notes_for_staff_and_recalculate_the_due_times()
    {
        var (admin, ho, staff) = await SetupAsync();
        var worker = await WorkerAsync(admin, ho);
        var ticket = await _s.GuestTicketAsync(staff);
        var id = ticket.GetProperty("id").GetGuid();

        var assigned = await _s.ActAsync(staff, id, "assign", new { assigneeId = worker.Id });
        Assert.Equal(worker.Id, assigned.GetProperty("assigneeId").GetGuid());
        Assert.Equal("Rina Worker", assigned.GetProperty("assigneeName").GetString());
        Assert.Contains(assigned.GetProperty("messages").EnumerateArray(), m => m.GetProperty("kind").GetString() == "Internal" && m.GetProperty("text").GetString()!.StartsWith("Assigned to Rina Worker", StringComparison.Ordinal));

        var before = ticket.GetProperty("resolveDueAt").GetDateTimeOffset();
        var urgent = await H.JsonAsync(HttpMethod.Put, $"{SupportHelper.TicketsUrl}/{id}/priority", HttpStatusCode.OK, new { priority = "Urgent" }, staff);
        Assert.Equal("Urgent", urgent.GetProperty("priority").GetString());
        Assert.Equal(TimeSpan.FromHours(8), urgent.GetProperty("resolveDueAt").GetDateTimeOffset() - urgent.GetProperty("createdAt").GetDateTimeOffset()); // Urgent: resolve in 8 h
        Assert.True(urgent.GetProperty("resolveDueAt").GetDateTimeOffset() < before);

        var unassigned = await _s.ActAsync(staff, id, "assign", new { assigneeId = (Guid?)null });
        Assert.Equal(JsonValueKind.Null, unassigned.GetProperty("assigneeId").ValueKind);
        Assert.Equal(HttpStatusCode.BadRequest, (await H.SendAsync(HttpMethod.Put, $"{SupportHelper.TicketsUrl}/{id}/priority", new { priority = 42 }, staff)).StatusCode);
    }

    [Fact]
    public async Task A_ticket_can_only_be_assigned_to_an_active_staff_member_who_may_work_on_it()
    {
        var (admin, ho, staff) = await SetupAsync();
        var id = (await _s.GuestTicketAsync(staff)).GetProperty("id").GetGuid();
        var noSupport = await H.Api.CreateRoleAsync(admin, StoreHelper.Unique("Role"), ["catalog.product.view"]);
        var user = await H.Api.CreateUserAsync(admin, $"{StoreHelper.Unique("ns")}@example.com", "No Support", false, (ho, noSupport));

        await StoreHelper.ExpectCodeAsync(await _s.TryActAsync(staff, id, "assign", new { assigneeId = user.Id }), "TICKET.ASSIGNEE_NOT_FOUND");
        await StoreHelper.ExpectCodeAsync(await _s.TryActAsync(staff, id, "assign", new { assigneeId = Guid.NewGuid() }), "TICKET.ASSIGNEE_NOT_FOUND");
    }

    [Fact]
    public async Task The_list_filters_by_status_priority_assignee_channel_category_order_dates_and_search()
    {
        var (admin, ho, staff) = await SetupAsync();
        var (orderNumber, mobile, _) = await _s.OrderAsync(admin);
        var worker = await WorkerAsync(admin, ho);
        var marker = StoreHelper.Unique("zq");

        var a = await _s.StaffCreateAsync(staff, mobile, orderNumber, $"Late {marker}", "x", "delivery", "High", worker.Id, "WhatsApp");
        var b = await _s.StaffCreateAsync(staff, StoreHelper.NewMobile(), null, $"Refund {marker}", "y", "payment", "Low");
        await _s.ActAsync(staff, b.GetProperty("id").GetGuid(), "resolve");

        async Task<List<string>> NumbersAsync(string query)
        {
            var json = await H.JsonAsync(HttpMethod.Get, $"{SupportHelper.TicketsUrl}?search={marker}&{query}", HttpStatusCode.OK, bearer: staff);
            return [.. json.GetProperty("items").EnumerateArray().Select(t => t.GetProperty("number").GetString()!)];
        }

        var na = a.GetProperty("number").GetString()!;
        var nb = b.GetProperty("number").GetString()!;
        Assert.Equal([na, nb], (await NumbersAsync("sort=number")).Order());
        Assert.Equal([nb], await NumbersAsync("status=Resolved"));
        Assert.Equal([na], await NumbersAsync("priority=High"));
        Assert.Equal([na], await NumbersAsync($"assigneeId={worker.Id}"));
        Assert.Equal([nb], await NumbersAsync("unassigned=true"));
        Assert.Equal([na], await NumbersAsync("channel=WhatsApp"));
        Assert.Equal([nb], await NumbersAsync("category=payment"));
        Assert.Equal([na], await NumbersAsync($"orderNumber={orderNumber}"));
        Assert.Equal([na], await NumbersAsync($"branchId={ho}"));
        Assert.Equal([na], await NumbersAsync("active=true"));
        Assert.Equal([na, nb], (await NumbersAsync($"from={OpsHelper.Today}&to={OpsHelper.Today}")).Order());
        Assert.Empty(await NumbersAsync($"to={OpsHelper.DaysAgo(2)}"));
        Assert.Equal([na], await H.JsonAsync(HttpMethod.Get, $"{SupportHelper.TicketsUrl}?search={mobile}", HttpStatusCode.OK, bearer: staff)
            .ContinueWith(t => t.Result.GetProperty("items").EnumerateArray().Select(x => x.GetProperty("number").GetString()!).ToList(), TaskScheduler.Default));

        var paged = await H.JsonAsync(HttpMethod.Get, $"{SupportHelper.TicketsUrl}?search={marker}&pageSize=1&page=2&sort=number", HttpStatusCode.OK, bearer: staff);
        Assert.Equal(2, paged.GetProperty("total").GetInt32());
        Assert.Equal(1, paged.GetProperty("items").GetArrayLength());
        Assert.Equal(2, paged.GetProperty("page").GetInt32());
        var summary = await H.JsonAsync(HttpMethod.Get, SupportHelper.TicketsUrl + "/summary", HttpStatusCode.OK, bearer: staff);
        Assert.True(summary.GetProperty("byStatus").GetProperty("Open").GetInt32() >= 1);
        Assert.True(summary.GetProperty("active").GetInt32() >= 1);
    }

    [Fact]
    public async Task A_ticket_whose_response_is_late_is_overdue()
    {
        var (_, _, staff) = await SetupAsync();
        var ticket = await _s.GuestTicketAsync(staff);
        var id = ticket.GetProperty("id").GetGuid();
        Assert.False(ticket.GetProperty("overdue").GetBoolean());

        await _s.DbAsync(db => db.Tickets.Where(t => t.Id == id).ExecuteUpdateAsync(s => s.SetProperty(t => t.ResponseDueAt, DateTimeOffset.UtcNow.AddHours(-1)), Ct));

        var late = await H.JsonAsync(HttpMethod.Get, $"{SupportHelper.TicketsUrl}?overdue=true&search={ticket.GetProperty("number").GetString()}", HttpStatusCode.OK, bearer: staff);
        Assert.Equal(1, late.GetProperty("total").GetInt32());
        Assert.True(late.GetProperty("items")[0].GetProperty("overdue").GetBoolean());
        await _s.ReplyAsync(staff, id, "Sorry for the wait.");
        var done = await H.JsonAsync(HttpMethod.Get, $"{SupportHelper.TicketsUrl}?overdue=true&search={ticket.GetProperty("number").GetString()}", HttpStatusCode.OK, bearer: staff);
        Assert.Equal(0, done.GetProperty("total").GetInt32());
    }

    // ---- bulk -----------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Bulk_assign_and_close_report_each_ticket_and_skip_the_ones_that_cannot_change()
    {
        var (admin, ho, staff) = await SetupAsync();
        var worker = await WorkerAsync(admin, ho);
        var ids = new List<Guid>();
        for (var i = 0; i < 3; i++)
        {
            ids.Add((await _s.GuestTicketAsync(staff)).GetProperty("id").GetGuid());
        }

        var assigned = await H.JsonAsync(HttpMethod.Post, SupportHelper.TicketsUrl + "/bulk", HttpStatusCode.OK, new { ids, action = "assign", assigneeId = worker.Id }, staff);
        Assert.Equal(3, assigned.GetProperty("succeeded").GetInt32());
        Assert.All(ids, id => Assert.Equal(worker.Id, _s.GetAsync(staff, id).Result.GetProperty("assigneeId").GetGuid()));

        await _s.ActAsync(staff, ids[0], "close");
        var missing = Guid.NewGuid();
        var closed = await H.JsonAsync(HttpMethod.Post, SupportHelper.TicketsUrl + "/bulk", HttpStatusCode.OK, new { ids = new[] { ids[0], ids[1], ids[2], missing }, action = "close", reason = "Spam" }, staff);

        Assert.Equal(4, closed.GetProperty("total").GetInt32());
        Assert.Equal(2, closed.GetProperty("succeeded").GetInt32());
        Assert.Equal(2, closed.GetProperty("failed").GetInt32());
        var items = closed.GetProperty("items").EnumerateArray().ToList();
        Assert.Equal("TICKET.INVALID_TRANSITION", items.Single(i => i.GetProperty("id").GetGuid() == ids[0]).GetProperty("code").GetString());
        Assert.Equal("TICKET.NOT_FOUND", items.Single(i => i.GetProperty("id").GetGuid() == missing).GetProperty("code").GetString());
        Assert.Equal("Closed", (await _s.GetAsync(staff, ids[1])).GetProperty("status").GetString());
        Assert.Equal("Spam", (await _s.GetAsync(staff, ids[1])).GetProperty("history").EnumerateArray().Last().GetProperty("reason").GetString());
    }

    [Fact]
    public async Task A_bulk_request_over_the_limit_or_without_tickets_or_with_an_unknown_action_is_refused()
    {
        var (_, _, staff) = await SetupAsync();

        var tooMany = Enumerable.Range(0, 51).Select(_ => Guid.NewGuid()).ToArray();
        await StoreHelper.ExpectCodeAsync(await H.SendAsync(HttpMethod.Post, SupportHelper.TicketsUrl + "/bulk", new { ids = tooMany, action = "close" }, staff), "TICKET.BULK_LIMIT");
        var fifty = Enumerable.Range(0, 50).Select(_ => Guid.NewGuid()).ToArray();
        var allowed = await H.JsonAsync(HttpMethod.Post, SupportHelper.TicketsUrl + "/bulk", HttpStatusCode.OK, new { ids = fifty, action = "close" }, staff);
        Assert.Equal(50, allowed.GetProperty("failed").GetInt32());
        await StoreHelper.ExpectCodeAsync(await H.SendAsync(HttpMethod.Post, SupportHelper.TicketsUrl + "/bulk", new { ids = Array.Empty<Guid>(), action = "close" }, staff), "GENERAL.VALIDATION");
        await StoreHelper.ExpectCodeAsync(await H.SendAsync(HttpMethod.Post, SupportHelper.TicketsUrl + "/bulk", new { ids = new[] { Guid.NewGuid() }, action = "delete" }, staff), "GENERAL.VALIDATION");
    }

    // ---- branches and permissions ----------------------------------------------------------------------------------

    [Fact]
    public async Task A_ticket_of_an_order_belongs_to_its_branch_and_looks_like_not_found_to_another_branch_while_a_global_ticket_is_open_to_both()
    {
        var (admin, ho, staffHo) = await SetupAsync();
        var other = await H.Api.CreateBranchAsync(admin, "S" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant(), "Support Branch B");
        var staffOther = await _s.StaffAsync(admin, other, SupportHelper.AllSupport);
        var (orderNumber, mobile, _) = await _s.OrderAsync(admin);
        var branchTicket = await _s.StaffCreateAsync(staffHo, mobile, orderNumber);
        var globalTicket = await _s.StaffCreateAsync(staffHo, StoreHelper.NewMobile());
        var id = branchTicket.GetProperty("id").GetGuid();
        Assert.Equal(ho, branchTicket.GetProperty("branchId").GetGuid());

        await StoreHelper.ExpectCodeAsync(await H.SendAsync(HttpMethod.Get, $"{SupportHelper.TicketsUrl}/{id}", bearer: staffOther), "TICKET.NOT_FOUND");
        await StoreHelper.ExpectCodeAsync(await _s.TryActAsync(staffOther, id, "reply", new { text = "hi" }), "TICKET.NOT_FOUND");
        await StoreHelper.ExpectCodeAsync(await _s.TryActAsync(staffOther, id, "close"), "TICKET.NOT_FOUND");
        await StoreHelper.ExpectCodeAsync(await _s.TryActAsync(staffOther, id, "assign", new { assigneeId = (Guid?)null }), "TICKET.NOT_FOUND");
        var listOther = await H.JsonAsync(HttpMethod.Get, $"{SupportHelper.TicketsUrl}?search={mobile}", HttpStatusCode.OK, bearer: staffOther);
        Assert.Equal(0, listOther.GetProperty("total").GetInt32());

        Assert.Equal(HttpStatusCode.OK, (await H.SendAsync(HttpMethod.Get, $"{SupportHelper.TicketsUrl}/{globalTicket.GetProperty("id").GetGuid()}", bearer: staffOther)).StatusCode);
        var replied = await _s.ReplyAsync(staffOther, globalTicket.GetProperty("id").GetGuid(), "Handled from branch B.");
        Assert.Equal("InProgress", replied.GetProperty("status").GetString());

        // Staff of the order's branch and global staff see the branch ticket.
        Assert.Equal(id, (await _s.GetAsync(staffHo, id)).GetProperty("id").GetGuid());
        Assert.Equal(id, (await _s.GetAsync(admin, id)).GetProperty("id").GetGuid());
        // A staff member of another branch cannot create a ticket on the order either.
        await StoreHelper.ExpectCodeAsync(await _s.TryStaffCreateAsync(staffOther, new { mobile, subject = "x", message = "y", orderNumber }), "TICKET.ORDER_NOT_FOUND");
    }

    [Fact]
    public async Task Every_action_needs_its_own_permission_in_the_branch_of_the_ticket()
    {
        var (admin, ho, staff) = await SetupAsync();
        var (orderNumber, mobile, _) = await _s.OrderAsync(admin);
        var id = (await _s.StaffCreateAsync(staff, mobile, orderNumber)).GetProperty("id").GetGuid();
        var viewer = await _s.StaffAsync(admin, ho, "support.ticket.view");
        var replier = await _s.StaffAsync(admin, ho, "support.ticket.view", "support.ticket.reply");
        var nobody = await _s.StaffAsync(admin, ho, "catalog.product.view");

        Assert.Equal(HttpStatusCode.Forbidden, (await H.SendAsync(HttpMethod.Get, $"{SupportHelper.TicketsUrl}/{id}", bearer: nobody)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await _s.TryActAsync(viewer, id, "reply", new { text = "x" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await _s.TryActAsync(viewer, id, "resolve")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await _s.TryActAsync(replier, id, "assign", new { assigneeId = (Guid?)null })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await _s.TryActAsync(replier, id, "close")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await H.SendAsync(HttpMethod.Put, $"{SupportHelper.TicketsUrl}/{id}/priority", new { priority = "High" }, replier)).StatusCode);
        // Closing through the generic status endpoint needs the close permission too, and setStatus on a reply as well.
        Assert.Equal(HttpStatusCode.Forbidden, (await _s.TryActAsync(replier, id, "status", new { status = "Closed" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await _s.TryActAsync(replier, id, "reply", new { text = "bye", setStatus = "Closed" })).StatusCode);
        Assert.Equal("Open", (await _s.GetAsync(staff, id)).GetProperty("status").GetString());

        Assert.Equal("Resolved", (await _s.ActAsync(replier, id, "resolve")).GetProperty("status").GetString());
        // In bulk each ticket is checked on its own: a replier cannot close.
        var bulk = await H.JsonAsync(HttpMethod.Post, SupportHelper.TicketsUrl + "/bulk", HttpStatusCode.OK, new { ids = new[] { id }, action = "close" }, replier);
        Assert.Equal(1, bulk.GetProperty("failed").GetInt32());
    }

    [Fact]
    public async Task The_support_endpoints_are_all_in_the_security_inventory_with_one_support_permission_each()
    {
        var sources = factory.Services.GetServices<Microsoft.AspNetCore.Routing.EndpointDataSource>();
        var routes = sources.SelectMany(s => s.Endpoints).OfType<Microsoft.AspNetCore.Routing.RouteEndpoint>()
            .Where(e => e.RoutePattern.RawText?.Contains("/support/", StringComparison.Ordinal) == true)
            .Select(e => new
            {
                Route = e.RoutePattern.RawText!,
                Policies = e.Metadata.GetOrderedMetadata<Microsoft.AspNetCore.Authorization.IAuthorizeData>().Select(a => a.Policy ?? "(default)").ToList(),
            }).ToList();

        Assert.True(routes.Count == 19, $"found {routes.Count} support endpoints, expected 13 ticket, 5 canned reply and 1 report endpoint");
        Assert.All(routes, r =>
        {
            var named = Assert.Single(r.Policies, p => p.StartsWith("perm-any:", StringComparison.Ordinal));
            Assert.StartsWith("perm-any:support.", named, StringComparison.Ordinal);
        });
    }
}
