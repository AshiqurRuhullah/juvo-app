using System.Net;
using System.Text.Json;
using Juvo.Api.IntegrationTests.Orders;
using Juvo.Api.IntegrationTests.Storefront;
using Juvo.Api.IntegrationTests.Support;
using Juvo.Application.Accounting;
using Juvo.Domain.Accounting;
using Juvo.Infrastructure.Outbox;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Juvo.Api.IntegrationTests.Operations;

public class ExpenseTests(OpsFactory factory) : IClassFixture<OpsFactory>
{
    private readonly OpsHelper _o = new(new OrderHelper(new StoreHelper(factory)));

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly string[] Author = ["accounting.expense.view", "accounting.expense.create", "accounting.expense.edit", "accounting.expense.delete", "accounting.cashbook.view", "accounting.cashbook.edit"];
    private static readonly string[] Approver = ["accounting.expense.view", "accounting.expense.approve", "accounting.cashbook.view"];

    private async Task<(string Admin, Guid Ho, Guid Category, Guid Account)> SetupAsync(decimal opening = 1000m)
    {
        var admin = await _o.H.AdminAsync();
        var ho = await _o.HoAsync();
        return (admin, ho, await _o.ExpenseCategoryAsync(admin), await _o.AccountAsync(admin, ho, opening));
    }

    // ---- the lifecycle ------------------------------------------------------------------------------------------

    [Fact]
    public async Task An_expense_is_drafted_submitted_approved_and_booked_into_the_cash_book_once()
    {
        var (admin, ho, category, account) = await SetupAsync();
        var author = await _o.StaffAsync(admin, ho, Author);
        var approver = await _o.StaffAsync(admin, ho, Approver);

        var draft = await _o.ExpenseAsync(author, category, account, 250m, reference: "Bill 77");
        var id = draft.GetProperty("id").GetGuid();
        Assert.Equal("Draft", draft.GetProperty("status").GetString());
        Assert.Matches(@"^EXP-HO-\d{4}-\d{6}$", draft.GetProperty("number").GetString());
        Assert.Equal(1000m, await _o.BalanceAsync(author, account)); // a draft books nothing

        var changed = await _o.PutAsync($"/api/v1/expenses/{id}", author, new { categoryId = category, expenseDate = OpsHelper.Today, amount = 300m, method = "Cash", accountId = account, reference = "Bill 77" });
        Assert.Equal(300m, changed.GetProperty("amount").GetDecimal());

        var submitted = await _o.PostAsync($"/api/v1/expenses/{id}/submit", author);
        Assert.Equal("Submitted", submitted.GetProperty("status").GetString());
        Assert.False(submitted.GetProperty("autoApproved").GetBoolean());
        Assert.Equal(1000m, await _o.BalanceAsync(author, account)); // still nothing booked

        // A submitted expense can no longer be edited.
        await OpsHelper.ExpectCodeAsync(await _o.Api.PutAsync($"/api/v1/expenses/{id}", new { categoryId = category, expenseDate = OpsHelper.Today, amount = 1m, method = "Cash", accountId = account }, author), "EXPENSE.NOT_EDITABLE");

        var approved = await _o.PostAsync($"/api/v1/expenses/{id}/approve", approver, new { note = "ok" });
        Assert.Equal("Approved", approved.GetProperty("status").GetString());
        Assert.Equal("ok", approved.GetProperty("decisionNote").GetString());
        Assert.Equal(700m, await _o.BalanceAsync(approver, account));

        // Approving again is refused and books nothing twice.
        await OpsHelper.ExpectCodeAsync(await _o.TryPostAsync($"/api/v1/expenses/{id}/approve", approver), "EXPENSE.INVALID_TRANSITION");
        Assert.Equal(700m, await _o.BalanceAsync(approver, account));
        var lines = await _o.GetAsync($"/api/v1/accounts/{account}/entries", approver);
        var line = Assert.Single(lines.GetProperty("items").EnumerateArray());
        Assert.Equal(-300m, line.GetProperty("amount").GetDecimal());
        Assert.Equal("Expense", line.GetProperty("sourceType").GetString());
    }

    [Fact]
    public async Task A_rejection_needs_a_reason_and_the_author_can_fix_and_send_it_again()
    {
        var (admin, ho, category, account) = await SetupAsync();
        var author = await _o.StaffAsync(admin, ho, Author);
        var approver = await _o.StaffAsync(admin, ho, Approver);
        var id = (await _o.ExpenseAsync(author, category, account, 90m)).GetProperty("id").GetGuid();
        await _o.PostAsync($"/api/v1/expenses/{id}/submit", author);

        await StoreHelper.ExpectCodeAsync(await _o.TryPostAsync($"/api/v1/expenses/{id}/reject", approver, new { reason = " " }), "GENERAL.VALIDATION");
        var rejected = await _o.PostAsync($"/api/v1/expenses/{id}/reject", approver, new { reason = "No receipt" });
        Assert.Equal("Rejected", rejected.GetProperty("status").GetString());
        Assert.Equal("No receipt", rejected.GetProperty("decisionNote").GetString());
        Assert.Equal(1000m, await _o.BalanceAsync(author, account));

        // Rejected can only go back to Draft, not straight to Approved.
        await OpsHelper.ExpectCodeAsync(await _o.TryPostAsync($"/api/v1/expenses/{id}/approve", approver), "EXPENSE.INVALID_TRANSITION");
        var draft = await _o.PostAsync($"/api/v1/expenses/{id}/return-to-draft", author);
        Assert.Equal("Draft", draft.GetProperty("status").GetString());
        await _o.PostAsync($"/api/v1/expenses/{id}/submit", author);
        Assert.Equal("Approved", (await _o.PostAsync($"/api/v1/expenses/{id}/approve", approver)).GetProperty("status").GetString());
    }

    [Fact]
    public async Task A_submitted_expense_can_be_withdrawn_and_only_a_draft_can_be_removed()
    {
        var (admin, ho, category, account) = await SetupAsync();
        var author = await _o.StaffAsync(admin, ho, Author);
        var id = (await _o.ExpenseAsync(author, category, account, 40m)).GetProperty("id").GetGuid();
        await _o.PostAsync($"/api/v1/expenses/{id}/submit", author);
        await OpsHelper.ExpectCodeAsync(await _o.Api.DeleteAsync($"/api/v1/expenses/{id}", author), "EXPENSE.NOT_EDITABLE");
        Assert.Equal("Draft", (await _o.PostAsync($"/api/v1/expenses/{id}/return-to-draft", author)).GetProperty("status").GetString());
        Assert.Equal(HttpStatusCode.NoContent, (await _o.Api.DeleteAsync($"/api/v1/expenses/{id}", author)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _o.TryGetAsync($"/api/v1/expenses/{id}", author)).StatusCode);
    }

    [Fact]
    public async Task You_cannot_approve_what_you_submitted_yourself_unless_you_are_the_super_admin()
    {
        var (admin, ho, category, account) = await SetupAsync();
        var both = await _o.StaffAsync(admin, ho, [.. Author, .. Approver]);
        var id = (await _o.ExpenseAsync(both, category, account, 20m)).GetProperty("id").GetGuid();
        await _o.PostAsync($"/api/v1/expenses/{id}/submit", both);
        await OpsHelper.ExpectCodeAsync(await _o.TryPostAsync($"/api/v1/expenses/{id}/approve", both), "EXPENSE.SELF_APPROVAL");

        var own = (await _o.ExpenseAsync(admin, category, account, 20m)).GetProperty("id").GetGuid();
        await _o.PostAsync($"/api/v1/expenses/{own}/submit", admin);
        Assert.Equal("Approved", (await _o.PostAsync($"/api/v1/expenses/{own}/approve", admin)).GetProperty("status").GetString());
    }

    [Fact]
    public async Task Expenses_at_or_below_the_approval_threshold_are_approved_on_submit()
    {
        var (admin, ho, category, account) = await SetupAsync(1000m);
        var author = await _o.StaffAsync(admin, ho, Author);
        await _o.SetSettingAsync("Expenses.ApprovalThreshold", "500");
        try
        {
            var small = (await _o.ExpenseAsync(author, category, account, 500m)).GetProperty("id").GetGuid();
            var autoApproved = await _o.PostAsync($"/api/v1/expenses/{small}/submit", author);
            Assert.Equal("Approved", autoApproved.GetProperty("status").GetString());
            Assert.True(autoApproved.GetProperty("autoApproved").GetBoolean());
            Assert.Equal(500m, await _o.BalanceAsync(author, account));

            var large = (await _o.ExpenseAsync(author, category, account, 500.01m)).GetProperty("id").GetGuid();
            Assert.Equal("Submitted", (await _o.PostAsync($"/api/v1/expenses/{large}/submit", author)).GetProperty("status").GetString());
            Assert.Equal(500m, await _o.BalanceAsync(author, account));
        }
        finally
        {
            await _o.ClearSettingAsync("Expenses.ApprovalThreshold");
        }
    }

    [Fact]
    public async Task Expense_input_is_validated_and_the_account_must_belong_to_the_branch()
    {
        var (admin, ho, category, account) = await SetupAsync();
        var other = await _o.Api.CreateBranchAsync(admin, "X" + Guid.NewGuid().ToString("N")[..6].ToUpperInvariant(), "Other branch");
        var otherAccount = await _o.AccountAsync(admin, other);
        var bad = async (object body) => await _o.Api.PostAsync("/api/v1/expenses", body, admin);

        Assert.Equal(HttpStatusCode.BadRequest, (await bad(new { branchId = ho, categoryId = category, expenseDate = OpsHelper.Today, amount = 0m, method = "Cash", accountId = account })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await bad(new { branchId = ho, categoryId = category, expenseDate = "2099-01-01", amount = 5m, method = "Cash", accountId = account })).StatusCode);
        await OpsHelper.ExpectCodeAsync(await bad(new { branchId = ho, categoryId = category, expenseDate = OpsHelper.Today, amount = 5m, method = "Cash", accountId = otherAccount }), "EXPENSE.ACCOUNT_BRANCH_MISMATCH");
        await OpsHelper.ExpectCodeAsync(await bad(new { branchId = ho, categoryId = Guid.NewGuid(), expenseDate = OpsHelper.Today, amount = 5m, method = "Cash", accountId = account }), "EXPENSE.CATEGORY_NOT_FOUND");
        await OpsHelper.ExpectCodeAsync(await bad(new { branchId = ho, categoryId = category, expenseDate = OpsHelper.Today, amount = 5m, method = "Cash", accountId = Guid.NewGuid() }), "EXPENSE.ACCOUNT_NOT_FOUND");
        await OpsHelper.ExpectCodeAsync(await bad(new { branchId = ho, categoryId = category, expenseDate = OpsHelper.Today, amount = 5m, method = "Cash", accountId = account, attachmentPaths = new[] { "expenses/9999/none.pdf" } }), "EXPENSE.ATTACHMENT_INVALID");
    }

    [Fact]
    public async Task Receipts_are_uploaded_checked_and_attached()
    {
        var (admin, ho, category, account) = await SetupAsync();
        var form = new MultipartFormDataContent();
        var fake = new ByteArrayContent("not an image"u8.ToArray());
        form.Add(fake, "file", "x.pdf");
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/expenses/uploads") { Content = form };
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", admin);
        Assert.Equal(HttpStatusCode.BadRequest, (await _o.Api.Client.SendAsync(request, Ct)).StatusCode); // judged by content, not name

        var pdf = new MultipartFormDataContent();
        pdf.Add(new ByteArrayContent("%PDF-1.4 receipt"u8.ToArray()), "file", "Rent receipt.pdf");
        var ok = new HttpRequestMessage(HttpMethod.Post, "/api/v1/expenses/uploads") { Content = pdf };
        ok.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", admin);
        var upload = await TestApi.ExpectAsync(await _o.Api.Client.SendAsync(ok, Ct), HttpStatusCode.Created);
        var path = upload.GetProperty("path").GetString()!;
        Assert.StartsWith("expenses/", path, StringComparison.Ordinal);

        var created = await _o.PostAsync("/api/v1/expenses", admin, new { branchId = ho, categoryId = category, expenseDate = OpsHelper.Today, amount = 15m, method = "Bank", accountId = account, attachmentPaths = new[] { path } }, HttpStatusCode.Created);
        var file = Assert.Single(created.GetProperty("attachments").EnumerateArray());
        Assert.Equal("application/pdf", file.GetProperty("contentType").GetString());
        Assert.Equal("Rentreceipt.pdf", file.GetProperty("fileName").GetString());
    }

    // ---- recurring ----------------------------------------------------------------------------------------------

    [Fact]
    public async Task Recurring_templates_generate_each_month_once_even_when_run_twice()
    {
        var (admin, ho, category, account) = await SetupAsync();
        var start = DateOnly.FromDateTime(DateTime.UtcNow.AddHours(6)).AddMonths(-2);
        start = new DateOnly(start.Year, start.Month, 1);
        var template = await _o.PostAsync("/api/v1/expenses/recurring", admin,
            new { branchId = ho, title = "Shop rent", categoryId = category, amount = 5000m, method = "Bank", accountId = account, dayOfMonth = 1, startDate = start.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture) }, HttpStatusCode.Created);
        var templateId = template.GetProperty("id").GetGuid();

        // Run through the background worker (called directly), then through the endpoint: nothing doubles.
        var worker = factory.Services.GetServices<IHostedService>().OfType<RecurringExpenseWorker>().Single();
        var first = await worker.SweepAsync(Ct);
        Assert.True(first.Created >= 3, $"expected at least the three months of this template, created {first.Created}");
        var again = await _o.PostAsync("/api/v1/expenses/recurring/run", admin);
        Assert.Equal(0, again.GetProperty("created").GetInt32());

        var list = await _o.GetAsync($"/api/v1/expenses?search=Shop%20rent&pageSize=50", admin);
        var generated = list.GetProperty("expenses").GetProperty("items").EnumerateArray().Where(e => e.GetProperty("isRecurring").GetBoolean() && e.GetProperty("reference").GetString() == "Shop rent").ToList();
        Assert.Equal(3, generated.Count);
        Assert.All(generated, e => Assert.Equal("Submitted", e.GetProperty("status").GetString()));
        Assert.Equal(3, generated.Select(e => e.GetProperty("expenseDate").GetString()).Distinct().Count());

        var db = await _o.WithDbAsync(async d => await d.Expenses.IgnoreQueryFilters().CountAsync(e => e.RecurringExpenseId == templateId, Ct));
        Assert.Equal(3, db);

        // A template that is switched off generates nothing more; the month key is unique in the database.
        await _o.PutAsync($"/api/v1/expenses/recurring/{templateId}", admin,
            new { branchId = ho, title = "Shop rent", categoryId = category, amount = 5000m, method = "Bank", accountId = account, dayOfMonth = 1, startDate = start.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture), isActive = false });
        Assert.Equal(0, (await _o.PostAsync("/api/v1/expenses/recurring/run", admin)).GetProperty("created").GetInt32());
        Assert.Contains((await _o.GetAsync("/api/v1/expenses/recurring", admin)).EnumerateArray(), t => t.GetProperty("id").GetGuid() == templateId && !t.GetProperty("isActive").GetBoolean());
    }

    [Fact]
    public void The_due_months_of_a_template_start_at_its_start_month_never_beyond_twelve_months_back_and_skip_what_was_done()
    {
        var today = new DateOnly(2026, 11, 12);
        var template = new RecurringExpense(Guid.NewGuid(), "Rent", new ExpenseDetails(Guid.NewGuid(), new DateOnly(2024, 1, 1), 10m, MoneyMethod.Cash, Guid.NewGuid(), null, null, null), 15, new DateOnly(2024, 1, 1), null);

        var months = RecurringExpenseService.DueMonths(template, today).Select(m => m.Period).ToList();
        Assert.Equal(["2511", "2512", "2601", "2602", "2603", "2604", "2605", "2606", "2607", "2608", "2609", "2610"], months); // 15 Nov is still ahead of 12 Nov

        template.MarkGenerated("2608");
        Assert.Equal(["2609", "2610"], RecurringExpenseService.DueMonths(template, today).Select(m => m.Period).ToList());

        var ended = new RecurringExpense(Guid.NewGuid(), "Short", new ExpenseDetails(Guid.NewGuid(), new DateOnly(2026, 8, 1), 10m, MoneyMethod.Cash, Guid.NewGuid(), null, null, null), 1, new DateOnly(2026, 8, 1), new DateOnly(2026, 9, 30));
        Assert.Equal(["2608", "2609"], RecurringExpenseService.DueMonths(ended, today).Select(m => m.Period).ToList());
    }

    // ---- accounts: transfers, adjustments, balances, day book ---------------------------------------------------

    [Fact]
    public async Task Transfers_and_adjustments_move_balances_and_the_day_book_adds_up()
    {
        var (admin, ho, category, cash) = await SetupAsync(1000m);
        var bank = await _o.AccountAsync(admin, ho, 5000m, "Bank");

        var transfer = await _o.PostAsync("/api/v1/accounts/transfers", admin, new { fromAccountId = cash, toAccountId = bank, amount = 300m, note = "Deposit" }, HttpStatusCode.Created);
        Assert.Equal(-300m, transfer.GetProperty("out").GetProperty("amount").GetDecimal());
        Assert.Equal(300m, transfer.GetProperty("in").GetProperty("amount").GetDecimal());
        Assert.Equal(transfer.GetProperty("transferId").GetGuid(), transfer.GetProperty("out").GetProperty("transferId").GetGuid());
        await _o.PostAsync("/api/v1/accounts/adjustments", admin, new { accountId = bank, amount = -50m, reason = "Bank fee" }, HttpStatusCode.Created);

        var expense = (await _o.ExpenseAsync(admin, category, cash, 120m)).GetProperty("id").GetGuid();
        await _o.PostAsync($"/api/v1/expenses/{expense}/submit", admin);
        await _o.PostAsync($"/api/v1/expenses/{expense}/approve", admin);

        Assert.Equal(580m, await _o.BalanceAsync(admin, cash)); // 1000 - 300 - 120
        Assert.Equal(5250m, await _o.BalanceAsync(admin, bank)); // 5000 + 300 - 50

        var balances = await _o.GetAsync($"/api/v1/accounts/balances?branchId={ho}&asOf={OpsHelper.Today}", admin);
        var accounts = balances.GetProperty("accounts").EnumerateArray().ToList();
        var cashRow = accounts.Single(a => a.GetProperty("id").GetGuid() == cash);
        Assert.Equal(1000m, cashRow.GetProperty("opening").GetDecimal());
        Assert.Equal(0m, cashRow.GetProperty("moneyIn").GetDecimal());
        Assert.Equal(420m, cashRow.GetProperty("moneyOut").GetDecimal());
        Assert.Equal(580m, cashRow.GetProperty("balance").GetDecimal());

        // The day book of the two accounts: entries are the four lines; totals are in minus out.
        var day = await _o.GetAsync($"/api/v1/accounts/daybook?accountId={cash}&from={OpsHelper.DaysAgo(1)}&to={OpsHelper.Today}", admin);
        Assert.Equal(2, day.GetProperty("entries").GetProperty("total").GetInt32());
        Assert.Equal(0m, day.GetProperty("totalIn").GetDecimal());
        Assert.Equal(420m, day.GetProperty("totalOut").GetDecimal());
        Assert.Equal(-420m, day.GetProperty("net").GetDecimal());
        var bankDay = await _o.GetAsync($"/api/v1/accounts/daybook?accountId={bank}&from={OpsHelper.DaysAgo(1)}&to={OpsHelper.Today}", admin);
        Assert.Equal(300m, bankDay.GetProperty("totalIn").GetDecimal());
        Assert.Equal(50m, bankDay.GetProperty("totalOut").GetDecimal());

        // Entries can never be edited or removed: there is no such endpoint, and the table refuses it.
        var entryCount = await _o.WithDbAsync(async d => await d.LedgerEntries.CountAsync(e => e.AccountId == cash || e.AccountId == bank, Ct));
        Assert.Equal(4, entryCount);
        await Assert.ThrowsAnyAsync<Exception>(() => _o.WithDbAsync(async d =>
        {
            var line = await d.LedgerEntries.FirstAsync(e => e.AccountId == cash, Ct);
            d.LedgerEntries.Remove(line);
            return await d.SaveChangesAsync(Ct);
        }));
    }

    [Fact]
    public async Task Customer_payments_and_refunds_are_read_into_the_day_book_but_never_copied_into_the_cash_book()
    {
        var pay = new Juvo.Api.IntegrationTests.Payments.PaymentHelper(_o.O);
        var (admin, product) = await pay.ProductAsync(500m);
        var ho = await _o.HoAsync();
        var account = await _o.AccountAsync(admin, ho, 0m);
        var before = await _o.WithDbAsync(async d => await d.LedgerEntries.CountAsync(Ct));

        var placed = await pay.PlaceAsync(product.Variants[0].Id, 2);
        await pay.PayAsync(placed);
        var paymentId = await _o.WithDbAsync(async d => await d.Payments.IgnoreQueryFilters(["BranchScope"]).Where(p => p.OrderId == placed.OrderId).Select(p => p.Id).SingleAsync(Ct));
        await pay.RefundAsync(admin, paymentId, 200m);

        var day = await _o.GetAsync($"/api/v1/accounts/daybook?branchId={ho}&from={OpsHelper.DaysAgo(1)}&to={OpsHelper.Today}&pageSize=100", admin);
        var entries = day.GetProperty("entries").GetProperty("items").EnumerateArray().ToList();
        var receipt = Assert.Single(entries, e => e.GetProperty("sourceId").GetGuid() == paymentId && e.GetProperty("kind").GetString() == "CustomerReceipt");
        Assert.True(receipt.GetProperty("derived").GetBoolean());
        Assert.Equal(placed.Total, receipt.GetProperty("amount").GetDecimal());
        Assert.Equal(JsonValueKind.Null, receipt.GetProperty("accountId").ValueKind);
        var refund = Assert.Single(entries, e => e.GetProperty("kind").GetString() == "Refund" && e.GetProperty("description").GetString()!.Contains(placed.Number, StringComparison.Ordinal));
        Assert.Equal(-200m, refund.GetProperty("amount").GetDecimal());

        // The totals equal an independent sum over the payment tables for the same period.
        var (paid, refunded, settled) = await _o.WithDbAsync(async d =>
        {
            var day0 = DateTime.UtcNow.AddHours(6).Date.AddDays(-1);
            var from = new DateTimeOffset(day0.Year, day0.Month, day0.Day, 0, 0, 0, TimeSpan.FromHours(6));
            var to = from.AddDays(2);
            var paidSum = await d.Payments.IgnoreQueryFilters(["BranchScope"]).Where(p => p.BranchId == ho && p.Kind != Juvo.Domain.Payments.PaymentKind.Cod && p.PaidAt >= from && p.PaidAt < to &&
                (p.Status == Juvo.Domain.Orders.PaymentStatus.Paid || p.Status == Juvo.Domain.Orders.PaymentStatus.PartiallyRefunded || p.Status == Juvo.Domain.Orders.PaymentStatus.Refunded)).SumAsync(p => (decimal?)p.Amount, Ct) ?? 0m;
            var refundSum = await d.Refunds.IgnoreQueryFilters(["BranchScope"]).Where(r => r.BranchId == ho && r.Status == Juvo.Domain.Payments.RefundState.Completed && r.CompletedAt >= from && r.CompletedAt < to).SumAsync(r => (decimal?)r.Amount, Ct) ?? 0m;
            var codSum = await d.CodSettlements.IgnoreQueryFilters(["BranchScope"]).Where(s => s.BranchId == ho && s.Status == Juvo.Domain.Couriers.SettlementStatus.Settled && s.SettledAt >= from && s.SettledAt < to).SumAsync(s => (decimal?)s.ReceivedTotal, Ct) ?? 0m;
            return (paidSum, refundSum, codSum);
        });
        // The day book also lists the branch cash book lines of the same days. Other tests of this class share the database and book
        // expenses on the same day, so the independent sum must include them or the result would depend on the order the tests run in.
        var fromDay = DateOnly.Parse(OpsHelper.DaysAgo(1), System.Globalization.CultureInfo.InvariantCulture);
        var toDay = DateOnly.Parse(OpsHelper.Today, System.Globalization.CultureInfo.InvariantCulture);
        var (ledgerIn, ledgerOut) = await _o.WithDbAsync(async d =>
        {
            var lines = d.LedgerEntries.IgnoreQueryFilters(["BranchScope"]).Where(e => e.BranchId == ho && e.EntryDate >= fromDay && e.EntryDate <= toDay);
            var inSum = await lines.Where(e => e.Amount > 0).SumAsync(e => (decimal?)e.Amount, Ct) ?? 0m;
            var outSum = await lines.Where(e => e.Amount < 0).SumAsync(e => (decimal?)e.Amount, Ct) ?? 0m;
            return (inSum, -outSum);
        });
        Assert.Equal(paid + settled + ledgerIn, day.GetProperty("totalIn").GetDecimal());
        Assert.Equal(refunded + ledgerOut, day.GetProperty("totalOut").GetDecimal());
        Assert.Equal(paid + settled + ledgerIn - refunded - ledgerOut, day.GetProperty("net").GetDecimal());

        // An account filter shows only cash book lines; nothing was ever written to the ledger by the payments.
        var accountDay = await _o.GetAsync($"/api/v1/accounts/daybook?accountId={account}&from={OpsHelper.DaysAgo(1)}&to={OpsHelper.Today}", admin);
        Assert.Equal(0, accountDay.GetProperty("entries").GetProperty("total").GetInt32());
        Assert.Equal(before, await _o.WithDbAsync(async d => await d.LedgerEntries.CountAsync(Ct)));
        var balances = await _o.GetAsync($"/api/v1/accounts/balances?branchId={ho}", admin);
        Assert.Equal(paid + settled - refunded, balances.GetProperty("derivedReceipts").GetDecimal());
    }

    [Fact]
    public async Task Transfer_and_adjustment_input_is_checked()
    {
        var (admin, ho, _, cash) = await SetupAsync(100m);
        var bank = await _o.AccountAsync(admin, ho, 0m, "Bank");
        await OpsHelper.ExpectCodeAsync(await _o.TryPostAsync("/api/v1/accounts/transfers", admin, new { fromAccountId = cash, toAccountId = cash, amount = 5m }), "EXPENSE.TRANSFER_INVALID");
        await OpsHelper.ExpectCodeAsync(await _o.TryPostAsync("/api/v1/accounts/transfers", admin, new { fromAccountId = cash, toAccountId = bank, amount = -5m }), "EXPENSE.TRANSFER_INVALID");
        Assert.Equal(HttpStatusCode.BadRequest, (await _o.TryPostAsync("/api/v1/accounts/adjustments", admin, new { accountId = cash, amount = 5m, reason = "" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await _o.TryPostAsync("/api/v1/accounts/adjustments", admin, new { accountId = cash, amount = 0m, reason = "none" })).StatusCode);

        await _o.PutAsync($"/api/v1/accounts/{bank}", admin, new { name = "Bank closed", type = "Bank", isActive = false });
        await OpsHelper.ExpectCodeAsync(await _o.TryPostAsync("/api/v1/accounts/transfers", admin, new { fromAccountId = cash, toAccountId = bank, amount = 5m }), "EXPENSE.ACCOUNT_INACTIVE");
    }

    // ---- permissions and branches -------------------------------------------------------------------------------

    [Fact]
    public async Task Branch_staff_only_see_and_touch_expenses_and_accounts_of_their_own_branch()
    {
        var (admin, ho, category, account) = await SetupAsync(100m);
        var branchB = await _o.Api.CreateBranchAsync(admin, "B" + Guid.NewGuid().ToString("N")[..6].ToUpperInvariant(), "Branch B");
        var staffB = await _o.StaffAsync(admin, branchB, [.. Author, .. Approver]);
        var staffHo = await _o.StaffAsync(admin, ho, [.. Author, .. Approver]);

        var hoExpense = (await _o.ExpenseAsync(admin, category, account, 11m)).GetProperty("id").GetGuid();
        var list = await _o.GetAsync("/api/v1/expenses", staffB);
        Assert.Empty(list.GetProperty("expenses").GetProperty("items").EnumerateArray());
        Assert.Equal(HttpStatusCode.NotFound, (await _o.TryGetAsync($"/api/v1/expenses/{hoExpense}", staffB)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _o.TryPostAsync($"/api/v1/expenses/{hoExpense}/submit", staffB)).StatusCode);

        // The account of another branch is invisible and cannot be spent from.
        var accounts = await _o.GetAsync("/api/v1/accounts", staffB);
        Assert.DoesNotContain(accounts.EnumerateArray(), a => a.GetProperty("id").GetGuid() == account);
        var denied = await _o.Api.PostAsync("/api/v1/expenses", new { branchId = ho, categoryId = category, expenseDate = OpsHelper.Today, amount = 5m, method = "Cash", accountId = account }, staffB);
        Assert.True(denied.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Forbidden);
        Assert.Equal(HttpStatusCode.NotFound, (await _o.TryGetAsync($"/api/v1/accounts/{account}/entries", staffB)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _o.Api.PutAsync($"/api/v1/accounts/{account}", new { name = "hijack", type = "Cash", isActive = true }, staffB)).StatusCode);

        // The head-office staff member sees it.
        Assert.Equal(HttpStatusCode.OK, (await _o.TryGetAsync($"/api/v1/expenses/{hoExpense}", staffHo)).StatusCode);
    }

    [Fact]
    public async Task Every_expense_and_account_action_needs_its_own_permission()
    {
        var (admin, ho, category, account) = await SetupAsync(100m);
        var viewer = await _o.StaffAsync(admin, ho, "accounting.expense.view", "accounting.cashbook.view");
        var id = (await _o.ExpenseAsync(admin, category, account, 12m)).GetProperty("id").GetGuid();

        Assert.Equal(HttpStatusCode.Forbidden, (await _o.Api.PostAsync("/api/v1/expenses", new { categoryId = category, expenseDate = OpsHelper.Today, amount = 5m, method = "Cash", accountId = account }, viewer)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await _o.TryPostAsync($"/api/v1/expenses/{id}/submit", viewer)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await _o.TryPostAsync($"/api/v1/expenses/{id}/approve", viewer)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await _o.Api.DeleteAsync($"/api/v1/expenses/{id}", viewer)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await _o.TryPostAsync("/api/v1/expenses/categories", viewer, new { nameEn = "Nope" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await _o.TryPostAsync("/api/v1/accounts", viewer, new { name = "Nope", type = "Cash", openingBalance = 0 })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await _o.TryPostAsync("/api/v1/accounts/transfers", viewer, new { fromAccountId = account, toAccountId = account, amount = 1 })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await _o.TryPostAsync("/api/v1/expenses/recurring/run", viewer)).StatusCode);

        var nobody = await _o.StaffAsync(admin, ho, "orders.order.view");
        Assert.Equal(HttpStatusCode.Forbidden, (await _o.TryGetAsync("/api/v1/expenses", nobody)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await _o.TryGetAsync("/api/v1/accounts/daybook", nobody)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await _o.Api.GetAsync("/api/v1/expenses")).StatusCode);
    }

    [Fact]
    public async Task Categories_have_one_level_unique_names_and_are_archived_not_deleted()
    {
        var admin = await _o.H.AdminAsync();
        var name = StoreHelper.Unique("Utilities");
        var parent = await _o.ExpenseCategoryAsync(admin, name);
        await OpsHelper.ExpectCodeAsync(await _o.TryPostAsync("/api/v1/expenses/categories", admin, new { nameEn = name }), "EXPENSE.CATEGORY_NAME_TAKEN");
        var child = (await _o.PostAsync("/api/v1/expenses/categories", admin, new { nameEn = "Electricity", parentId = parent }, HttpStatusCode.Created)).GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.BadRequest, (await _o.TryPostAsync("/api/v1/expenses/categories", admin, new { nameEn = "Too deep", parentId = child })).StatusCode);

        var archived = await _o.PutAsync($"/api/v1/expenses/categories/{child}", admin, new { nameEn = "Electricity", parentId = parent, isActive = false });
        Assert.False(archived.GetProperty("isActive").GetBoolean());
        var ho = await _o.HoAsync();
        var account = await _o.AccountAsync(admin, ho);
        await OpsHelper.ExpectCodeAsync(await _o.Api.PostAsync("/api/v1/expenses", new { branchId = ho, categoryId = child, expenseDate = OpsHelper.Today, amount = 5m, method = "Cash", accountId = account }, admin), "EXPENSE.CATEGORY_INACTIVE");
        Assert.DoesNotContain((await _o.GetAsync("/api/v1/expenses/categories", admin)).EnumerateArray(), c => c.GetProperty("id").GetGuid() == child);
        Assert.Contains((await _o.GetAsync("/api/v1/expenses/categories?includeInactive=true", admin)).EnumerateArray(), c => c.GetProperty("id").GetGuid() == child);
    }

    [Fact]
    public async Task The_expense_list_filters_searches_and_totals()
    {
        var (admin, ho, category, account) = await SetupAsync();
        var marker = StoreHelper.Unique("REF");
        foreach (var amount in new[] { 10m, 20m, 30m })
        {
            await _o.ExpenseAsync(admin, category, account, amount, reference: marker);
        }

        var all = await _o.GetAsync($"/api/v1/expenses?search={marker}", admin);
        Assert.Equal(3, all.GetProperty("expenses").GetProperty("total").GetInt32());
        Assert.Equal(60m, all.GetProperty("totalAmount").GetDecimal());
        var sorted = await _o.GetAsync($"/api/v1/expenses?search={marker}&sort=amount", admin);
        Assert.Equal([10m, 20m, 30m], sorted.GetProperty("expenses").GetProperty("items").EnumerateArray().Select(e => e.GetProperty("amount").GetDecimal()).ToArray());
        Assert.Equal(0, (await _o.GetAsync($"/api/v1/expenses?search={marker}&status=Approved", admin)).GetProperty("expenses").GetProperty("total").GetInt32());
        Assert.Equal(0, (await _o.GetAsync($"/api/v1/expenses?search={marker}&from={OpsHelper.DaysAgo(-5)}", admin)).GetProperty("expenses").GetProperty("total").GetInt32());
        Assert.Equal(3, (await _o.GetAsync($"/api/v1/expenses?search={marker}&accountId={account}&categoryId={category}&branchId={ho}", admin)).GetProperty("expenses").GetProperty("total").GetInt32());
    }
}
