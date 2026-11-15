using System.Globalization;
using Juvo.Application.Abstractions;
using Juvo.Application.Inventory;
using Juvo.Application.Operations;
using Juvo.Application.Orders;
using Juvo.Domain.Accounting;
using Juvo.Shared.Results;
using Juvo.Shared.Security;
using Microsoft.EntityFrameworkCore;

namespace Juvo.Application.Accounting;

/// <summary>
/// Monthly expense templates (rent, salaries, internet) and the generator behind them (module X). The generator creates one submitted expense per template and month, once: a unique
/// index on template and month keeps it idempotent even when two runs overlap, and months never generated in the past are not made up beyond twelve months back.
/// </summary>
public sealed class RecurringExpenseService(IApplicationDbContext db, IUnitOfWork uow, InventoryAccess access, ExpenseCore core, TimeProvider clock)
{
    private const int MaxCatchUpMonths = 12;

    private static Error NotFound() => Error.NotFound(ErrorCodes.Expense.RecurringNotFound, "Recurring expense not found.");

    public async Task<Result<IReadOnlyList<RecurringExpenseDto>>> ListAsync(Guid? branchId, CancellationToken cancellationToken)
    {
        var actor = await access.ActorAsync(cancellationToken);
        if (actor.IsFailure)
        {
            return actor.Error!;
        }

        var visible = InventoryAccess.VisibleBranches(actor.Value, Permissions.Accounting.ExpenseView);
        var query = from r in db.RecurringExpenses.AsNoTracking()
                    join c in db.ExpenseCategories.AsNoTracking() on r.CategoryId equals c.Id
                    join a in db.FinanceAccounts.AsNoTracking() on r.AccountId equals a.Id
                    join b in db.Branches.AsNoTracking() on r.BranchId equals b.Id
                    select new { Template = r, Category = c.NameEn, Account = a.Name, BranchCode = b.Code };
        if (visible is not null)
        {
            query = query.Where(x => visible.Contains(x.Template.BranchId));
        }

        if (branchId is { } id)
        {
            query = query.Where(x => x.Template.BranchId == id);
        }

        var rows = await query.OrderBy(x => x.BranchCode).ThenBy(x => x.Template.Title).ToListAsync(cancellationToken);
        return rows.Select(x => ToDto(x.Template, x.BranchCode, x.Category, x.Account)).ToList();
    }

    private static RecurringExpenseDto ToDto(RecurringExpense r, string branchCode, string category, string account) =>
        new(r.Id, r.BranchId, branchCode, r.Title, r.CategoryId, category, r.Amount, r.Method, r.AccountId, account, r.SupplierId, r.Notes, r.DayOfMonth, r.StartDate, r.EndDate, r.IsActive, r.LastGeneratedPeriod);

    private async Task<Result<ExpenseDetails>> ValidateAsync(Guid branchId, SaveRecurringExpenseRequest r, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(r.Title) || Ops.TooLong(r.Title, 160) || Ops.TooLong(r.Notes, 1000) || r.Amount <= 0 || r.Amount > 100_000_000m || !Enum.IsDefined(r.Method))
        {
            return Ops.Invalid("title", "Give a title (up to 160 characters) and an amount between 0.01 and 100,000,000.");
        }

        if (r.DayOfMonth is < 1 or > 28)
        {
            return Ops.Invalid("dayOfMonth", "Choose a day from 1 to 28 so that every month has it.");
        }

        if (r.EndDate is { } end && end < r.StartDate)
        {
            return Ops.Invalid("endDate", "The end date must not be before the start date.");
        }

        var category = await db.ExpenseCategories.AsNoTracking().FirstOrDefaultAsync(c => c.Id == r.CategoryId, cancellationToken);
        if (category is null)
        {
            return Error.NotFound(ErrorCodes.Expense.CategoryNotFound, "Category not found.");
        }

        var account = await db.FinanceAccounts.AsNoTracking().FirstOrDefaultAsync(a => a.Id == r.AccountId, cancellationToken);
        if (account is null)
        {
            return Error.NotFound(ErrorCodes.Expense.AccountNotFound, "Account not found.");
        }

        if (account.BranchId != branchId)
        {
            return Error.Conflict(ErrorCodes.Expense.AccountBranchMismatch, "The account belongs to another branch.");
        }

        if (r.SupplierId is { } supplierId && !await db.Suppliers.AnyAsync(s => s.Id == supplierId, cancellationToken))
        {
            return Error.NotFound(ErrorCodes.Purchase.SupplierNotFound, "Supplier not found.");
        }

        return new ExpenseDetails(r.CategoryId, r.StartDate, Math.Round(r.Amount, 2, MidpointRounding.AwayFromZero), r.Method, r.AccountId, r.SupplierId, null, r.Notes);
    }

    public async Task<Result<RecurringExpenseDto>> CreateAsync(SaveRecurringExpenseRequest request, CancellationToken cancellationToken)
    {
        var target = await access.TargetAsync(request.BranchId, Permissions.Accounting.ExpenseEdit, cancellationToken);
        if (target.IsFailure)
        {
            return target.Error!;
        }

        var details = await ValidateAsync(target.Value.Branch.Id, request, cancellationToken);
        if (details.IsFailure)
        {
            return details.Error!;
        }

        var template = new RecurringExpense(target.Value.Branch.Id, request.Title, details.Value, request.DayOfMonth, request.StartDate, request.EndDate);
        db.RecurringExpenses.Add(template);
        await db.SaveChangesAsync(cancellationToken);
        return await DtoAsync(template, cancellationToken);
    }

    public async Task<Result<RecurringExpenseDto>> UpdateAsync(Guid id, SaveRecurringExpenseRequest request, CancellationToken cancellationToken)
    {
        var actor = await access.ActorAsync(cancellationToken);
        if (actor.IsFailure)
        {
            return actor.Error!;
        }

        var template = await db.RecurringExpenses.FirstOrDefaultAsync(r => r.Id == id, cancellationToken);
        if (template is null)
        {
            return NotFound();
        }

        var refusal = await access.CheckAsync(actor.Value, template.BranchId, Permissions.Accounting.ExpenseEdit, cancellationToken);
        if (refusal is not null)
        {
            return refusal.Type == ErrorType.NotFound ? NotFound() : refusal;
        }

        var details = await ValidateAsync(template.BranchId, request, cancellationToken);
        if (details.IsFailure)
        {
            return details.Error!;
        }

        template.Update(request.Title, details.Value, request.DayOfMonth, request.StartDate, request.EndDate);
        if (request.IsActive is { } active)
        {
            template.SetActive(active);
        }

        await db.SaveChangesAsync(cancellationToken);
        return await DtoAsync(template, cancellationToken);
    }

    private async Task<RecurringExpenseDto> DtoAsync(RecurringExpense r, CancellationToken cancellationToken)
    {
        var code = await db.Branches.AsNoTracking().Where(b => b.Id == r.BranchId).Select(b => b.Code).FirstAsync(cancellationToken);
        var category = await db.ExpenseCategories.AsNoTracking().Where(c => c.Id == r.CategoryId).Select(c => c.NameEn).FirstAsync(cancellationToken);
        var account = await db.FinanceAccounts.AsNoTracking().Where(a => a.Id == r.AccountId).Select(a => a.Name).FirstAsync(cancellationToken);
        return ToDto(r, code, category, account);
    }

    /// <summary>Runs the generator for the branches the actor may edit expenses in (the "run now" button).</summary>
    public async Task<Result<GenerateRecurringResult>> RunNowAsync(CancellationToken cancellationToken)
    {
        var actor = await access.ActorAsync(cancellationToken);
        if (actor.IsFailure)
        {
            return actor.Error!;
        }

        return await GenerateDueAsync(InventoryAccess.VisibleBranches(actor.Value, Permissions.Accounting.ExpenseEdit), cancellationToken);
    }

    /// <summary>Creates every expense that is due and does not exist yet. <paramref name="branches"/> null means all branches (the background job).</summary>
    public async Task<GenerateRecurringResult> GenerateDueAsync(List<Guid>? branches, CancellationToken cancellationToken)
    {
        var today = Ops.BstDate(clock.GetUtcNow());
        var query = db.RecurringExpenses.IgnoreQueryFilters().AsNoTracking().Where(r => r.IsActive && r.StartDate <= today);
        if (branches is not null)
        {
            query = query.Where(r => branches.Contains(r.BranchId));
        }

        var templates = await query.OrderBy(r => r.BranchId).ThenBy(r => r.Title).ToListAsync(cancellationToken);
        int created = 0, auto = 0;
        foreach (var template in templates)
        {
            foreach (var (period, date) in DueMonths(template, today))
            {
                var result = await GenerateOneAsync(template.Id, period, date, cancellationToken);
                if (result is { } autoApproved)
                {
                    created++;
                    if (autoApproved)
                    {
                        auto++;
                    }
                }
            }
        }

        return new GenerateRecurringResult(templates.Count, created, auto);
    }

    /// <summary>The months of a template that are due and not yet generated, oldest first.</summary>
    public static IEnumerable<(string Period, DateOnly Date)> DueMonths(RecurringExpense t, DateOnly today)
    {
        var first = new DateOnly(t.StartDate.Year, t.StartDate.Month, 1);
        var earliest = new DateOnly(today.Year, today.Month, 1).AddMonths(-MaxCatchUpMonths);
        var month = first < earliest ? earliest : first;
        for (; month <= today; month = month.AddMonths(1))
        {
            var due = new DateOnly(month.Year, month.Month, t.DayOfMonth);
            var period = ExpensesService.Period(due);
            if (due > today || due < t.StartDate || (t.EndDate is { } end && due > end))
            {
                continue;
            }

            if (t.LastGeneratedPeriod is { } last && string.CompareOrdinal(period, last) <= 0)
            {
                continue;
            }

            yield return (period, due);
        }
    }

    private async Task<bool?> GenerateOneAsync(Guid templateId, string period, DateOnly date, CancellationToken cancellationToken)
    {
        try
        {
            return await uow.ExecuteInTransactionAsync<bool?>(async ct =>
            {
                var template = await db.RecurringExpenses.IgnoreQueryFilters().FirstAsync(r => r.Id == templateId, ct);
                if (await db.Expenses.IgnoreQueryFilters().AnyAsync(e => e.RecurringExpenseId == templateId && e.RecurringPeriod == period, ct))
                {
                    template.MarkGenerated(Latest(template.LastGeneratedPeriod, period));
                    await db.SaveChangesAsync(ct);
                    return null; // already there: nothing to do
                }

                var account = await db.FinanceAccounts.IgnoreQueryFilters().FirstAsync(a => a.Id == template.AccountId, ct);
                var category = await db.ExpenseCategories.FirstOrDefaultAsync(c => c.Id == template.CategoryId, ct);
                if (!account.IsActive || category is not { IsActive: true })
                {
                    return null; // the template points at something retired; the owner fixes the template
                }

                var branch = await db.Branches.FirstAsync(b => b.Id == template.BranchId, ct);
                var details = new ExpenseDetails(template.CategoryId, date, template.Amount, template.Method, template.AccountId, template.SupplierId, template.Title, template.Notes);
                var expense = await core.CreateAsync(branch, details, template.Id, period, ct);
                var autoApproved = await core.SubmitAsync(expense, account, Who.System, ct);
                template.MarkGenerated(Latest(template.LastGeneratedPeriod, period));
                await db.SaveChangesAsync(ct);
                return autoApproved;
            }, cancellationToken);
        }
        catch (DbUpdateException)
        {
            db.DiscardUnsavedAdditions(); // a parallel run created it first: the unique index kept it single
            return null;
        }
    }

    private static string Latest(string? current, string candidate) => current is not null && string.CompareOrdinal(current, candidate) > 0 ? current : candidate;
}
