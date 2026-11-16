using System.Net;
using System.Text.Json;
using Juvo.Api.IntegrationTests.Storefront;
using Microsoft.EntityFrameworkCore;

namespace Juvo.Api.IntegrationTests.Operations;

/// <summary>
/// The support report (FR-Y05) against a hand-made set of six tickets whose times are fixed in the database, so every figure can be added up by hand. Each test class has its own database,
/// so no other ticket is in these numbers.
/// </summary>
public class SupportReportTests(OpsFactory factory) : IClassFixture<OpsFactory>
{
    private readonly SupportHelper _s = new(factory);

    private StoreHelper H => _s.H;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static DateOnly Day(int daysAgo) => DateOnly.FromDateTime(DateTime.UtcNow.AddHours(6)).AddDays(-daysAgo);

    private static DateTimeOffset At(int daysAgo, int hour, int minute = 0) => new(Day(daysAgo).Year, Day(daysAgo).Month, Day(daysAgo).Day, hour, minute, 0, TimeSpan.FromHours(6));

    private sealed record Seeded(string Admin, Guid Ho, string Staff, Guid WorkerA, Guid WorkerB, Guid OrderTicket, Guid OtherBranch, string OtherStaff);

    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static Seeded? _seeded;

    private async Task SetTimesAsync(Guid id, DateTimeOffset created, DateTimeOffset? first = null, DateTimeOffset? resolved = null, DateTimeOffset? responseDue = null, DateTimeOffset? resolveDue = null) =>
        await _s.DbAsync(db => db.Tickets.Where(t => t.Id == id).ExecuteUpdateAsync(s => s
            .SetProperty(t => t.CreatedAt, created)
            .SetProperty(t => t.FirstResponseAt, first)
            .SetProperty(t => t.ResolvedAt, resolved)
            .SetProperty(t => t.ResponseDueAt, t => responseDue ?? t.ResponseDueAt)
            .SetProperty(t => t.ResolveDueAt, t => resolveDue ?? t.ResolveDueAt), Ct));

    private async Task<Seeded> SeedAsync()
    {
        await Gate.WaitAsync(Ct);
        try
        {
            if (_seeded is not null)
            {
                return _seeded;
            }

            var admin = await H.AdminAsync();
            var ho = await _s.O.HoAsync();
            var staff = await _s.StaffAsync(admin, ho, SupportHelper.AllSupport);
            var role = await H.Api.CreateRoleAsync(admin, StoreHelper.Unique("Role"), "support.ticket.view", "support.ticket.reply");
            var a = await H.Api.CreateUserAsync(admin, $"{StoreHelper.Unique("wa")}@example.com", "Anika Agent", false, (ho, role));
            var b = await H.Api.CreateUserAsync(admin, $"{StoreHelper.Unique("wb")}@example.com", "Babul Agent", false, (ho, role));
            var (orderNumber, orderMobile, _) = await _s.OrderAsync(admin);

            // T1 delivery, Anika: first reply after 30 min, resolved after 4 h (three days ago).
            var t1 = (await _s.StaffCreateAsync(staff, StoreHelper.NewMobile(), subject: "T1", category: "delivery", assignee: a.Id)).GetProperty("id").GetGuid();
            await _s.ReplyAsync(staff, t1, "Looking");
            await _s.ActAsync(staff, t1, "resolve");
            await SetTimesAsync(t1, At(3, 10), At(3, 10, 30), At(3, 14));

            // T2 payment, Anika: first reply after 90 min, resolved after 10 h, on the next day.
            var t2 = (await _s.StaffCreateAsync(staff, StoreHelper.NewMobile(), subject: "T2", category: "payment", assignee: a.Id)).GetProperty("id").GetGuid();
            await _s.ReplyAsync(staff, t2, "Checking");
            await _s.ActAsync(staff, t2, "resolve");
            await SetTimesAsync(t2, At(2, 20), At(2, 21, 30), At(1, 6));

            // T3 delivery, nobody, never answered, four days old and past both due times: the oldest and the only overdue one.
            var t3 = (await _s.StaffCreateAsync(staff, StoreHelper.NewMobile(), subject: "T3", category: "delivery")).GetProperty("id").GetGuid();
            await SetTimesAsync(t3, At(4, 9), responseDue: At(3, 9), resolveDue: At(1, 9));

            // T4 order, Babul: answered after 60 min, now waiting for the customer.
            var t4 = (await _s.StaffCreateAsync(staff, StoreHelper.NewMobile(), subject: "T4", category: "order", assignee: b.Id)).GetProperty("id").GetGuid();
            await _s.ReplyAsync(staff, t4, "Please send a photo", setStatus: "Waiting");
            await SetTimesAsync(t4, At(1, 11), At(1, 12));

            // T5 other, urgent, nobody, open.
            var t5 = (await _s.StaffCreateAsync(staff, StoreHelper.NewMobile(), subject: "T5", category: "other", priority: "Urgent")).GetProperty("id").GetGuid();
            await SetTimesAsync(t5, At(1, 15));

            // T6 order of Head Office (a branch ticket), nobody, open.
            var t6 = (await _s.StaffCreateAsync(staff, orderMobile, orderNumber, subject: "T6", category: "order")).GetProperty("id").GetGuid();
            await SetTimesAsync(t6, At(2, 9));

            var other = await H.Api.CreateBranchAsync(admin, "S" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant(), "Report Branch B");
            var otherStaff = await _s.StaffAsync(admin, other, SupportHelper.AllSupport);
            _seeded = new Seeded(admin, ho, staff, a.Id, b.Id, t6, other, otherStaff);
            return _seeded;
        }
        finally
        {
            Gate.Release();
        }
    }

    private Task<JsonElement> ReportAsync(string bearer, string query = "") =>
        H.JsonAsync(HttpMethod.Get, $"/api/v1/support/report?from={Day(5):yyyy-MM-dd}&to={Day(0):yyyy-MM-dd}{query}", HttpStatusCode.OK, bearer: bearer);

    private static Dictionary<string, int> Counts(JsonElement list) => list.EnumerateArray().ToDictionary(c => c.GetProperty("key").GetString()!, c => c.GetProperty("count").GetInt32());

    [Fact]
    public async Task The_consolidated_report_equals_the_hand_calculation()
    {
        var seeded = await SeedAsync();

        var r = await ReportAsync(seeded.Admin);

        Assert.Equal(6, r.GetProperty("newTickets").GetInt32());
        Assert.Equal(2, r.GetProperty("resolvedTickets").GetInt32());
        Assert.Equal(4, r.GetProperty("openNow").GetInt32());
        Assert.Equal(1, r.GetProperty("overdueNow").GetInt32());
        Assert.Equal(new Dictionary<string, int> { ["Open"] = 3, ["InProgress"] = 0, ["Waiting"] = 1 }, Counts(r.GetProperty("openByStatus")));
        Assert.Equal(new Dictionary<string, int> { ["Low"] = 0, ["Normal"] = 3, ["High"] = 0, ["Urgent"] = 1 }, Counts(r.GetProperty("openByPriority")));

        var first = r.GetProperty("firstResponse");
        Assert.Equal(3, first.GetProperty("count").GetInt32());
        Assert.Equal(60d, first.GetProperty("averageMinutes").GetDouble()); // (30 + 90 + 60) / 3
        Assert.Equal(60d, first.GetProperty("medianMinutes").GetDouble());
        var resolution = r.GetProperty("resolution");
        Assert.Equal(2, resolution.GetProperty("count").GetInt32());
        Assert.Equal(420d, resolution.GetProperty("averageMinutes").GetDouble()); // (240 + 600) / 2
        Assert.Equal(420d, resolution.GetProperty("medianMinutes").GetDouble());

        var categories = r.GetProperty("byCategory").EnumerateArray().Select(c => (c.GetProperty("key").GetString(), c.GetProperty("count").GetInt32())).ToList();
        Assert.Equal([("delivery", 2), ("order", 2), ("other", 1), ("payment", 1)], categories);

        var assignees = r.GetProperty("byAssignee").EnumerateArray().Select(a => (a.GetProperty("name").GetString(), a.GetProperty("total").GetInt32(), a.GetProperty("open").GetInt32(), a.GetProperty("resolved").GetInt32())).ToList();
        Assert.Equal([("Unassigned", 3, 3, 0), ("Anika Agent", 2, 0, 2), ("Babul Agent", 1, 1, 0)], assignees);

        var daily = r.GetProperty("daily").EnumerateArray().ToDictionary(d => d.GetProperty("date").GetString()!, d => (d.GetProperty("new").GetInt32(), d.GetProperty("resolved").GetInt32()));
        Assert.Equal(6, daily.Count);
        Assert.Equal((0, 0), daily[$"{Day(5):yyyy-MM-dd}"]);
        Assert.Equal((1, 0), daily[$"{Day(4):yyyy-MM-dd}"]);
        Assert.Equal((1, 1), daily[$"{Day(3):yyyy-MM-dd}"]);
        Assert.Equal((2, 0), daily[$"{Day(2):yyyy-MM-dd}"]);
        Assert.Equal((2, 1), daily[$"{Day(1):yyyy-MM-dd}"]);
        Assert.Equal((0, 0), daily[$"{Day(0):yyyy-MM-dd}"]);
        Assert.Equal(r.GetProperty("newTickets").GetInt32(), daily.Values.Sum(v => v.Item1));
        Assert.Equal(r.GetProperty("resolvedTickets").GetInt32(), daily.Values.Sum(v => v.Item2));

        var oldest = r.GetProperty("oldestOpen").EnumerateArray().Select(o => o.GetProperty("subject").GetString()).ToList();
        Assert.Equal(["T3", "T6", "T4", "T5"], oldest);
        Assert.True(r.GetProperty("oldestOpen")[0].GetProperty("ageHours").GetDouble() > 3 * 24);
    }

    [Fact]
    public async Task A_period_that_holds_only_some_of_the_tickets_counts_only_those()
    {
        var seeded = await SeedAsync();

        var r = await H.JsonAsync(HttpMethod.Get, $"/api/v1/support/report?from={Day(3):yyyy-MM-dd}&to={Day(3):yyyy-MM-dd}", HttpStatusCode.OK, bearer: seeded.Admin);

        Assert.Equal(1, r.GetProperty("newTickets").GetInt32()); // T1
        Assert.Equal(1, r.GetProperty("resolvedTickets").GetInt32());
        Assert.Equal(1, r.GetProperty("firstResponse").GetProperty("count").GetInt32());
        Assert.Equal(30d, r.GetProperty("firstResponse").GetProperty("averageMinutes").GetDouble());
        Assert.Equal(240d, r.GetProperty("resolution").GetProperty("medianMinutes").GetDouble());
        Assert.Equal(4, r.GetProperty("openNow").GetInt32()); // the backlog does not depend on the period
        Assert.Equal(1, r.GetProperty("daily").GetArrayLength());

        var empty = await H.JsonAsync(HttpMethod.Get, $"/api/v1/support/report?from={Day(40):yyyy-MM-dd}&to={Day(30):yyyy-MM-dd}", HttpStatusCode.OK, bearer: seeded.Admin);
        Assert.Equal(0, empty.GetProperty("newTickets").GetInt32());
        Assert.Equal(JsonValueKind.Null, empty.GetProperty("firstResponse").GetProperty("averageMinutes").ValueKind);
        Assert.Equal(JsonValueKind.Null, empty.GetProperty("resolution").GetProperty("medianMinutes").ValueKind);
    }

    [Fact]
    public async Task One_branch_report_holds_only_the_tickets_of_that_branch()
    {
        var seeded = await SeedAsync();

        var ho = await ReportAsync(seeded.Admin, $"&branchId={seeded.Ho}");

        Assert.Equal(1, ho.GetProperty("newTickets").GetInt32()); // T6, the only ticket of an order
        Assert.Equal(1, ho.GetProperty("openNow").GetInt32());
        Assert.Equal(seeded.Ho, ho.GetProperty("branchId").GetGuid());
        var other = await ReportAsync(seeded.Admin, $"&branchId={seeded.OtherBranch}");
        Assert.Equal(0, other.GetProperty("newTickets").GetInt32());
        Assert.Equal(0, other.GetProperty("openNow").GetInt32());
    }

    [Fact]
    public async Task Staff_of_one_branch_get_their_branch_and_the_global_tickets_and_cannot_ask_for_another_branch()
    {
        var seeded = await SeedAsync();

        var theirs = await ReportAsync(seeded.OtherStaff);
        Assert.Equal(5, theirs.GetProperty("newTickets").GetInt32()); // the five global tickets, not T6 of Head Office
        Assert.Equal(3, theirs.GetProperty("openNow").GetInt32());
        Assert.Equal(HttpStatusCode.NotFound, (await H.SendAsync(HttpMethod.Get, $"/api/v1/support/report?branchId={seeded.Ho}", bearer: seeded.OtherStaff)).StatusCode);

        var ho = await ReportAsync(seeded.Staff);
        Assert.Equal(6, ho.GetProperty("newTickets").GetInt32());
    }

    [Fact]
    public async Task The_report_needs_its_permission_and_a_sensible_period()
    {
        var seeded = await SeedAsync();
        var noReport = await _s.StaffAsync(seeded.Admin, seeded.Ho, "support.ticket.view");

        Assert.Equal(HttpStatusCode.Forbidden, (await H.SendAsync(HttpMethod.Get, "/api/v1/support/report", bearer: noReport)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await H.SendAsync(HttpMethod.Get, "/api/v1/support/report")).StatusCode);
        await StoreHelper.ExpectCodeAsync(await H.SendAsync(HttpMethod.Get, $"/api/v1/support/report?from={Day(0):yyyy-MM-dd}&to={Day(3):yyyy-MM-dd}", bearer: seeded.Admin), "GENERAL.VALIDATION");
        await StoreHelper.ExpectCodeAsync(await H.SendAsync(HttpMethod.Get, $"/api/v1/support/report?from={Day(400):yyyy-MM-dd}&to={Day(0):yyyy-MM-dd}", bearer: seeded.Admin), "GENERAL.VALIDATION");

        var thirty = await H.JsonAsync(HttpMethod.Get, "/api/v1/support/report", HttpStatusCode.OK, bearer: seeded.Admin); // the default is the last 30 days
        Assert.Equal(30, thirty.GetProperty("daily").GetArrayLength());
    }
}
