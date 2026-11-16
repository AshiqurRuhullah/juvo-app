using Juvo.Api.Configuration;
using Juvo.Api.Http;
using Juvo.Application.Accounting;
using Juvo.Domain.Accounting;
using Juvo.Shared.Pagination;
using Juvo.Shared.Security;
using Microsoft.AspNetCore.Mvc;

namespace Juvo.Api.Endpoints;

/// <summary>
/// Expenses, categories, recurring templates, cash and bank accounts, transfers and the day book (module X). Every endpoint names its permission; the services prove it again in the
/// branch of the record, so another branch's data looks like "not found".
/// </summary>
internal static class AccountingEndpoints
{
    public static WebApplication MapAccountingEndpoints(this WebApplication app)
    {
        var v1 = app.MapApiV1();
        MapExpenses(v1.MapGroup("/expenses").WithTags("Expenses"));
        MapAccounts(v1.MapGroup("/accounts").WithTags("Accounts and cash book"));
        return app;
    }

    private static void MapExpenses(RouteGroupBuilder g)
    {
        g.MapGet("", async ([AsParameters] ListParams list, ExpenseStatus? status, Guid? categoryId, Guid? branchId, Guid? accountId, Guid? supplierId, DateOnly? from, DateOnly? to, ExpensesService s, CancellationToken ct) =>
                (await s.ListAsync(list.ToRequest(), new ExpenseListQuery(status, categoryId, branchId, accountId, supplierId, from, to), ct)).ToHttpResult())
            .RequirePermission(Permissions.Accounting.ExpenseView).Produces<ExpenseListDto>()
            .WithSummary("Expenses of the branches where you may view them, newest first, with the total of the filtered rows. Filters: status, category, branch, account, supplier, date range; search number, reference, notes; sort date, amount, number, status.");

        g.MapGet("/{id:guid}", async (Guid id, ExpensesService s, CancellationToken ct) => (await s.GetAsync(id, ct)).ToHttpResult())
            .RequirePermission(Permissions.Accounting.ExpenseView).Produces<ExpenseDetailDto>().WithSummary("One expense with its receipts and the statuses it may move to.");

        g.MapPost("", async (SaveExpenseRequest r, ExpensesService s, CancellationToken ct) => (await s.CreateAsync(r, ct)).ToHttpResult(v => Results.Created($"/api/v1/expenses/{v.Id}", v)))
            .RequirePermission(Permissions.Accounting.ExpenseCreate).Produces<ExpenseDetailDto>(StatusCodes.Status201Created)
            .WithSummary("Record an expense as a draft in a branch (the active branch unless branchId is given). Attach receipts by sending the paths returned by the upload endpoint.");

        g.MapPut("/{id:guid}", async (Guid id, SaveExpenseRequest r, ExpensesService s, CancellationToken ct) => (await s.UpdateAsync(id, r, ct)).ToHttpResult())
            .RequirePermission(Permissions.Accounting.ExpenseEdit).Produces<ExpenseDetailDto>().WithSummary("Change a draft expense.");

        g.MapDelete("/{id:guid}", async (Guid id, ExpensesService s, CancellationToken ct) => (await s.DeleteAsync(id, ct)).ToHttpResult())
            .RequirePermission(Permissions.Accounting.ExpenseDelete).Produces(StatusCodes.Status204NoContent).WithSummary("Remove a draft expense (archived, never erased).");

        g.MapPost("/uploads", async (IFormFile file, ExpensesService s, CancellationToken ct) =>
            {
                await using var stream = file.OpenReadStream();
                return (await s.UploadAsync(stream, file.FileName, ct)).ToHttpResult(v => Results.Created(v.Url, v));
            })
            .DisableAntiforgery().WithMetadata(new RequestSizeLimitAttribute(10 * 1024 * 1024)).RequirePermission(Permissions.Accounting.ExpenseCreate).Produces<AttachmentUploadDto>(StatusCodes.Status201Created)
            .WithSummary("Upload a receipt or bill (JPEG, PNG, WebP or PDF, up to 8 MB). Send the returned path in attachmentPaths when saving the expense.");

        g.MapPost("/{id:guid}/submit", async (Guid id, ExpensesService s, CancellationToken ct) => (await s.SubmitAsync(id, ct)).ToHttpResult())
            .RequirePermission(Permissions.Accounting.ExpenseCreate).Produces<ExpenseDetailDto>()
            .WithSummary("Send a draft for approval. At or below the approval threshold setting (when above zero) it is approved at once and booked into the cash book.");

        g.MapPost("/{id:guid}/approve", async (Guid id, DecideExpenseRequest r, ExpensesService s, CancellationToken ct) => (await s.ApproveAsync(id, r, ct)).ToHttpResult())
            .RequirePermission(Permissions.Accounting.ExpenseApprove).Produces<ExpenseDetailDto>()
            .WithSummary("Approve a submitted expense. It is booked into the cash book as an outflow from its account. You cannot approve what you submitted yourself (Super Admin excepted).");

        g.MapPost("/{id:guid}/reject", async (Guid id, RejectExpenseRequest r, ExpensesService s, CancellationToken ct) => (await s.RejectAsync(id, r, ct)).ToHttpResult())
            .RequirePermission(Permissions.Accounting.ExpenseApprove).Produces<ExpenseDetailDto>().WithSummary("Reject a submitted expense with a reason.");

        g.MapPost("/{id:guid}/return-to-draft", async (Guid id, ExpensesService s, CancellationToken ct) => (await s.ReturnToDraftAsync(id, ct)).ToHttpResult())
            .RequirePermission(Permissions.Accounting.ExpenseCreate).Produces<ExpenseDetailDto>().WithSummary("Take a submitted expense back, or reopen a rejected one, to fix it and send it again.");

        g.MapGet("/categories", async (bool? includeInactive, ExpensesService s, CancellationToken ct) => (await s.CategoriesAsync(includeInactive == true, ct)).ToHttpResult())
            .RequirePermission(Permissions.Accounting.ExpenseView).Produces<IReadOnlyList<ExpenseCategoryDto>>().WithSummary("Expense categories (one level of sub-categories) with how many expenses use each.");

        g.MapPost("/categories", async (SaveExpenseCategoryRequest r, ExpensesService s, CancellationToken ct) => (await s.CreateCategoryAsync(r, ct)).ToHttpResult(v => Results.Created($"/api/v1/expenses/categories/{v.Id}", v)))
            .RequirePermission(Permissions.Accounting.ExpenseEdit).Produces<ExpenseCategoryDto>(StatusCodes.Status201Created).WithSummary("Add a category.");

        g.MapPut("/categories/{id:guid}", async (Guid id, SaveExpenseCategoryRequest r, ExpensesService s, CancellationToken ct) => (await s.UpdateCategoryAsync(id, r, ct)).ToHttpResult())
            .RequirePermission(Permissions.Accounting.ExpenseEdit).Produces<ExpenseCategoryDto>().WithSummary("Rename a category, move it, or archive it with isActive=false (categories in use are archived, never deleted).");

        g.MapGet("/recurring", async (Guid? branchId, RecurringExpenseService s, CancellationToken ct) => (await s.ListAsync(branchId, ct)).ToHttpResult())
            .RequirePermission(Permissions.Accounting.ExpenseView).Produces<IReadOnlyList<RecurringExpenseDto>>().WithSummary("Monthly expense templates of the branches you may view.");

        g.MapPost("/recurring", async (SaveRecurringExpenseRequest r, RecurringExpenseService s, CancellationToken ct) => (await s.CreateAsync(r, ct)).ToHttpResult(v => Results.Created($"/api/v1/expenses/recurring/{v.Id}", v)))
            .RequirePermission(Permissions.Accounting.ExpenseEdit).Produces<RecurringExpenseDto>(StatusCodes.Status201Created)
            .WithSummary("Add a monthly template (day 1 to 28, start and optional end date). A background job creates one submitted expense per month.");

        g.MapPut("/recurring/{id:guid}", async (Guid id, SaveRecurringExpenseRequest r, RecurringExpenseService s, CancellationToken ct) => (await s.UpdateAsync(id, r, ct)).ToHttpResult())
            .RequirePermission(Permissions.Accounting.ExpenseEdit).Produces<RecurringExpenseDto>().WithSummary("Change a template or switch it off with isActive=false.");

        g.MapPost("/recurring/run", async (RecurringExpenseService s, CancellationToken ct) => (await s.RunNowAsync(ct)).ToHttpResult())
            .RequirePermission(Permissions.Accounting.ExpenseEdit).Produces<GenerateRecurringResult>()
            .WithSummary("Run the generator now for your branches. Safe to repeat: a month that already has its expense is skipped.");
    }

    private static void MapAccounts(RouteGroupBuilder g)
    {
        g.MapGet("", async (Guid? branchId, bool? includeInactive, AccountsService s, CancellationToken ct) => (await s.ListAsync(branchId, includeInactive == true, ct)).ToHttpResult())
            .RequirePermission(Permissions.Accounting.CashbookView).Produces<IReadOnlyList<AccountDto>>().WithSummary("Cash, bank and mobile-banking accounts of the branches you may view, with their current balance.");

        g.MapPost("", async (SaveAccountRequest r, AccountsService s, CancellationToken ct) => (await s.CreateAsync(r, ct)).ToHttpResult(v => Results.Created($"/api/v1/accounts/{v.Id}", v)))
            .RequirePermission(Permissions.Accounting.CashbookEdit).Produces<AccountDto>(StatusCodes.Status201Created).WithSummary("Add an account with its opening balance to a branch.");

        g.MapPut("/{id:guid}", async (Guid id, UpdateAccountRequest r, AccountsService s, CancellationToken ct) => (await s.UpdateAsync(id, r, ct)).ToHttpResult())
            .RequirePermission(Permissions.Accounting.CashbookEdit).Produces<AccountDto>().WithSummary("Rename an account or switch it off (the opening balance is fixed; correct it with an adjustment).");

        g.MapGet("/{id:guid}/entries", async (Guid id, [AsParameters] ListParams list, AccountsService s, CancellationToken ct) => (await s.EntriesAsync(id, list.ToRequest(), ct)).ToHttpResult())
            .RequirePermission(Permissions.Accounting.CashbookView).Produces<PagedResult<LedgerEntryDto>>().WithSummary("The append-only cash book lines of one account, newest first.");

        g.MapPost("/transfers", async (TransferRequest r, AccountsService s, CancellationToken ct) => (await s.TransferAsync(r, ct)).ToHttpResult(v => Results.Created($"/api/v1/accounts/{r.FromAccountId}/entries", v)))
            .RequirePermission(Permissions.Accounting.CashbookEdit).Produces<TransferResultDto>(StatusCodes.Status201Created).WithSummary("Move money between two accounts: two cash book lines that share a transfer id.");

        g.MapPost("/adjustments", async (AdjustmentRequest r, AccountsService s, CancellationToken ct) => (await s.AdjustAsync(r, ct)).ToHttpResult(v => Results.Created($"/api/v1/accounts/{r.AccountId}/entries", v)))
            .RequirePermission(Permissions.Accounting.CashbookEdit).Produces<LedgerEntryDto>(StatusCodes.Status201Created).WithSummary("Correct an account with a signed amount and a reason. Lines are never edited or removed.");

        g.MapGet("/balances", async (DateOnly? asOf, Guid? branchId, AccountsService s, CancellationToken ct) => (await s.BalancesAsync(asOf, branchId, ct)).ToHttpResult())
            .RequirePermission(Permissions.Accounting.CashbookView).Produces<AccountBalancesDto>().WithSummary("Balance of every account as of a date: opening balance plus money in minus money out, and the net of money read from the payment data.");

        g.MapGet("/daybook", async ([AsParameters] ListParams list, DateOnly? from, DateOnly? to, Guid? branchId, Guid? accountId, AccountsService s, CancellationToken ct) =>
                (await s.DaybookAsync(list.ToRequest(), new DaybookQuery(from, to, branchId, accountId), ct)).ToHttpResult())
            .RequirePermission(Permissions.Accounting.CashbookView).Produces<DaybookDto>()
            .WithSummary("Day book for a date range (default the last 30 days): cash book lines plus customer payments, settled COD cash and completed refunds read from the payment data, newest first, with totals in, out and net.");
    }
}
