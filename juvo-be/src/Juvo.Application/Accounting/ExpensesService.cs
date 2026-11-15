using System.Globalization;
using Juvo.Application.Abstractions;
using Juvo.Application.Inventory;
using Juvo.Application.Operations;
using Juvo.Application.Orders;
using Juvo.Domain.Accounting;
using Juvo.Domain.Branches;
using Juvo.Shared.Pagination;
using Juvo.Shared.Results;
using Juvo.Shared.Security;
using Microsoft.EntityFrameworkCore;

namespace Juvo.Application.Accounting;

/// <summary>
/// The rules every expense follows, whoever creates it (a person or the recurring job): numbering, the approval threshold and the booking into the cash book. Methods change tracked
/// entities and do not save; the caller saves inside its transaction.
/// </summary>
public sealed class ExpenseCore(IApplicationDbContext db, NumberAllocator numbers, LedgerWriter ledger, OpsSettingsService settings, TimeProvider clock)
{
    public async Task<Expense> CreateAsync(Branch branch, ExpenseDetails details, Guid? recurringId, string? period, CancellationToken cancellationToken)
    {
        var number = await numbers.NextForBranchAsync(branch, "EXP", clock.GetUtcNow(), cancellationToken);
        var expense = new Expense(number, branch.Id, details, recurringId, period);
        db.Expenses.Add(expense);
        return expense;
    }

    /// <summary>Submits the expense. At or below the approval threshold (setting <c>Expenses.ApprovalThreshold</c>, when it is above zero) it is approved on the spot. Returns true when it was.</summary>
    public async Task<bool> SubmitAsync(Expense expense, FinanceAccount account, Who who, CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        expense.Submit(now, who.Id);
        var threshold = (await settings.ReadAsync(cancellationToken)).ExpenseApprovalThreshold;
        if (threshold > 0 && expense.Amount <= threshold)
        {
            Approve(expense, account, Who.System, "Below the approval threshold", auto: true);
            return true;
        }

        return false;
    }

    public void Approve(Expense expense, FinanceAccount account, Who who, string? note, bool auto)
    {
        expense.Approve(clock.GetUtcNow(), who.Id, who.Name, note, auto);
        ledger.Add(account, expense.ExpenseDate, LedgerEntryKind.Expense, -expense.Amount, $"Expense {expense.Number}" + (string.IsNullOrWhiteSpace(expense.Reference) ? string.Empty : $" ({expense.Reference})"), "Expense", expense.Id, null, who.Name);
    }
}

/// <summary>
/// Expense categories and expenses of the branches (module X): create, edit while draft, submit, approve or reject with a reason, receipts as attachments. Approval books an outflow into the
/// expense's account. Reads and writes are proven in the expense's own branch (another branch's expense looks like "not found").
/// </summary>
public sealed class ExpensesService(
    IApplicationDbContext db, IUnitOfWork uow, InventoryAccess access, OrderAccess orderAccess, ExpenseCore core, AttachmentStore attachments, TimeProvider clock)
{
    private const string Folder = "expenses";

    private static Error NotFound() => Error.NotFound(ErrorCodes.Expense.NotFound, "Expense not found.");

    // ---- Categories --------------------------------------------------------------------------------------

    public async Task<Result<IReadOnlyList<ExpenseCategoryDto>>> CategoriesAsync(bool includeInactive, CancellationToken cancellationToken)
    {
        var query = db.ExpenseCategories.AsNoTracking().AsQueryable();
        if (!includeInactive)
        {
            query = query.Where(c => c.IsActive);
        }

        var counts = await db.Expenses.AsNoTracking().GroupBy(e => e.CategoryId).Select(g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.Count, cancellationToken);
        var rows = await query.OrderBy(c => c.NameEn).ToListAsync(cancellationToken);
        return rows.Select(c => new ExpenseCategoryDto(c.Id, c.NameEn, c.NameBn, c.ParentId, c.IsActive, counts.GetValueOrDefault(c.Id))).ToList();
    }

    private async Task<Error?> ValidateCategoryAsync(Guid? id, SaveExpenseCategoryRequest r, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(r.NameEn) || Ops.TooLong(r.NameEn, 120) || Ops.TooLong(r.NameBn, 120))
        {
            return Ops.Invalid("nameEn", "Give the category a name (up to 120 characters).");
        }

        if (r.ParentId is { } parentId)
        {
            if (parentId == id || !await db.ExpenseCategories.AnyAsync(c => c.Id == parentId, cancellationToken))
            {
                return Error.NotFound(ErrorCodes.Expense.CategoryNotFound, "The parent category was not found.");
            }

            // Only one level (tree-light): a parent cannot itself have a parent.
            if (await db.ExpenseCategories.AnyAsync(c => c.Id == parentId && c.ParentId != null, cancellationToken) ||
                (id is { } own && await db.ExpenseCategories.AnyAsync(c => c.ParentId == own, cancellationToken)))
            {
                return Ops.Invalid("parentId", "Categories have one level of sub-categories.");
            }
        }

        var name = r.NameEn.Trim();
        return await db.ExpenseCategories.AnyAsync(c => c.NameEn == name && c.ParentId == r.ParentId && c.Id != id, cancellationToken)
            ? Error.Conflict(ErrorCodes.Expense.CategoryNameTaken, "A category with this name already exists here.")
            : null;
    }

    public async Task<Result<ExpenseCategoryDto>> CreateCategoryAsync(SaveExpenseCategoryRequest request, CancellationToken cancellationToken)
    {
        if (await ValidateCategoryAsync(null, request, cancellationToken) is { } error)
        {
            return error;
        }

        var category = new ExpenseCategory(request.NameEn, request.NameBn, request.ParentId);
        db.ExpenseCategories.Add(category);
        await db.SaveChangesAsync(cancellationToken);
        return new ExpenseCategoryDto(category.Id, category.NameEn, category.NameBn, category.ParentId, true, 0);
    }

    public async Task<Result<ExpenseCategoryDto>> UpdateCategoryAsync(Guid id, SaveExpenseCategoryRequest request, CancellationToken cancellationToken)
    {
        var category = await db.ExpenseCategories.FirstOrDefaultAsync(c => c.Id == id, cancellationToken);
        if (category is null)
        {
            return Error.NotFound(ErrorCodes.Expense.CategoryNotFound, "Category not found.");
        }

        if (await ValidateCategoryAsync(id, request, cancellationToken) is { } error)
        {
            return error;
        }

        category.Update(request.NameEn, request.NameBn, request.ParentId);
        if (request.IsActive is { } active)
        {
            category.SetActive(active);
        }

        await db.SaveChangesAsync(cancellationToken);
        var count = await db.Expenses.CountAsync(e => e.CategoryId == id, cancellationToken);
        return new ExpenseCategoryDto(category.Id, category.NameEn, category.NameBn, category.ParentId, category.IsActive, count);
    }

    // ---- Reading -----------------------------------------------------------------------------------------

    private static readonly IReadOnlySet<string> SortFields = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "date", "amount", "number", "status" };

    public async Task<Result<ExpenseListDto>> ListAsync(PageRequest request, ExpenseListQuery filter, CancellationToken cancellationToken)
    {
        var actor = await access.ActorAsync(cancellationToken);
        if (actor.IsFailure)
        {
            return actor.Error!;
        }

        var visible = InventoryAccess.VisibleBranches(actor.Value, Permissions.Accounting.ExpenseView);
        var query = from e in db.Expenses.AsNoTracking()
                    join c in db.ExpenseCategories.AsNoTracking() on e.CategoryId equals c.Id
                    join a in db.FinanceAccounts.AsNoTracking() on e.AccountId equals a.Id
                    join b in db.Branches.AsNoTracking() on e.BranchId equals b.Id
                    select new { Expense = e, Category = c.NameEn, Account = a.Name, BranchCode = b.Code };
        if (visible is not null)
        {
            query = query.Where(x => visible.Contains(x.Expense.BranchId));
        }

        if (filter.Status is { } status)
        {
            query = query.Where(x => x.Expense.Status == status);
        }

        if (filter.CategoryId is { } categoryId)
        {
            query = query.Where(x => x.Expense.CategoryId == categoryId);
        }

        if (filter.BranchId is { } branchId)
        {
            query = query.Where(x => x.Expense.BranchId == branchId);
        }

        if (filter.AccountId is { } accountId)
        {
            query = query.Where(x => x.Expense.AccountId == accountId);
        }

        if (filter.SupplierId is { } supplierId)
        {
            query = query.Where(x => x.Expense.SupplierId == supplierId);
        }

        if (filter.From is { } from)
        {
            query = query.Where(x => x.Expense.ExpenseDate >= from);
        }

        if (filter.To is { } to)
        {
            query = query.Where(x => x.Expense.ExpenseDate <= to);
        }

        if (!string.IsNullOrWhiteSpace(request.Search))
        {
            var term = request.Search.Trim();
            query = query.Where(x => x.Expense.Number.Contains(term) || (x.Expense.Reference != null && x.Expense.Reference.Contains(term)) || (x.Expense.Notes != null && x.Expense.Notes.Contains(term)));
        }

        var sort = request.ParseSort(SortFields) is { Count: > 0 } f ? f[0] : null;
        query = (sort?.Name.ToLowerInvariant(), sort?.Descending ?? true) switch
        {
            ("amount", true) => query.OrderByDescending(x => x.Expense.Amount),
            ("amount", false) => query.OrderBy(x => x.Expense.Amount),
            ("number", true) => query.OrderByDescending(x => x.Expense.Number),
            ("number", false) => query.OrderBy(x => x.Expense.Number),
            ("status", true) => query.OrderByDescending(x => x.Expense.Status).ThenByDescending(x => x.Expense.ExpenseDate),
            ("status", false) => query.OrderBy(x => x.Expense.Status).ThenByDescending(x => x.Expense.ExpenseDate),
            (_, false) => query.OrderBy(x => x.Expense.ExpenseDate),
            _ => query.OrderByDescending(x => x.Expense.ExpenseDate).ThenByDescending(x => x.Expense.CreatedAt),
        };

        var page = request.Normalized;
        var total = await query.CountAsync(cancellationToken);
        var sum = await query.SumAsync(x => (decimal?)x.Expense.Amount, cancellationToken) ?? 0m;
        var rows = await query.Skip(page.Skip).Take(page.PageSize).Select(x => new { x.Expense, x.Category, x.Account, x.BranchCode, Files = x.Expense.Attachments.Count }).ToListAsync(cancellationToken);
        var items = rows.Select(x => new ExpenseListItemDto(x.Expense.Id, x.Expense.Number, x.Expense.BranchId, x.BranchCode, x.Expense.CategoryId, x.Category, x.Expense.ExpenseDate, x.Expense.Amount, x.Expense.Method,
            x.Expense.AccountId, x.Account, x.Expense.SupplierId, x.Expense.Reference, x.Expense.Status, x.Expense.AutoApproved, x.Expense.RecurringExpenseId != null, x.Files, x.Expense.CreatedAt)).ToList();
        return new ExpenseListDto(new PagedResult<ExpenseListItemDto>(items, total, page.Page, page.PageSize), sum);
    }

    private Task<Expense?> LoadAsync(Guid id, CancellationToken cancellationToken) =>
        db.Expenses.Include(e => e.Attachments).FirstOrDefaultAsync(e => e.Id == id, cancellationToken);

    private async Task<Error?> CheckAsync(Ums.Actor actor, Guid branchId, string permission, CancellationToken cancellationToken)
    {
        var refusal = await access.CheckAsync(actor, branchId, permission, cancellationToken);
        return refusal is { Type: ErrorType.NotFound } ? NotFound() : refusal;
    }

    public async Task<Result<ExpenseDetailDto>> GetAsync(Guid id, CancellationToken cancellationToken)
    {
        var actor = await access.ActorAsync(cancellationToken);
        if (actor.IsFailure)
        {
            return actor.Error!;
        }

        var expense = await LoadAsync(id, cancellationToken);
        if (expense is null)
        {
            return NotFound();
        }

        return await CheckAsync(actor.Value, expense.BranchId, Permissions.Accounting.ExpenseView, cancellationToken) is { } refusal ? refusal : await DetailAsync(expense, cancellationToken);
    }

    private async Task<ExpenseDetailDto> DetailAsync(Expense e, CancellationToken cancellationToken)
    {
        var branchCode = await db.Branches.AsNoTracking().Where(b => b.Id == e.BranchId).Select(b => b.Code).FirstAsync(cancellationToken);
        var category = await db.ExpenseCategories.AsNoTracking().Where(c => c.Id == e.CategoryId).Select(c => c.NameEn).FirstAsync(cancellationToken);
        var account = await db.FinanceAccounts.AsNoTracking().Where(a => a.Id == e.AccountId).Select(a => a.Name).FirstAsync(cancellationToken);
        var supplier = e.SupplierId is { } sid ? await db.Suppliers.AsNoTracking().Where(s => s.Id == sid).Select(s => s.Name).FirstOrDefaultAsync(cancellationToken) : null;
        return new ExpenseDetailDto(e.Id, e.Number, e.BranchId, branchCode, e.CategoryId, category, e.ExpenseDate, e.Amount, e.Method, e.AccountId, account, e.SupplierId, supplier, e.Reference, e.Notes, e.Status,
            e.AutoApproved, e.RecurringExpenseId, e.CreatedAt, e.SubmittedAt, e.DecidedAt, e.DecidedByName, e.DecisionNote,
            [.. e.Attachments.OrderBy(a => a.CreatedAt).Select(a => new ExpenseAttachmentDto(a.Id, a.FileName, a.ContentType, a.Size, attachments.UrlOf(a.Path)))],
            [.. ExpenseStateMachine.NextFor(e.Status).Select(s => s.ToString())]);
    }

    // ---- Writing -----------------------------------------------------------------------------------------

    private async Task<Result<ExpenseDetails>> ValidateAsync(Guid branchId, SaveExpenseRequest r, CancellationToken cancellationToken)
    {
        if (r.Amount <= 0 || r.Amount > 100_000_000m || !Enum.IsDefined(r.Method) || Ops.TooLong(r.Reference, 120) || Ops.TooLong(r.Notes, 1000))
        {
            return Ops.Invalid("amount", "The amount must be between 0.01 and 100,000,000; the reference can have 120 and the notes 1000 characters.");
        }

        var today = Ops.BstDate(clock.GetUtcNow());
        if (r.ExpenseDate > today.AddDays(1) || r.ExpenseDate < today.AddYears(-5))
        {
            return Ops.Invalid("expenseDate", "Choose a date within the last five years that is not in the future.");
        }

        var category = await db.ExpenseCategories.AsNoTracking().FirstOrDefaultAsync(c => c.Id == r.CategoryId, cancellationToken);
        if (category is null)
        {
            return Error.NotFound(ErrorCodes.Expense.CategoryNotFound, "Category not found.");
        }

        if (!category.IsActive)
        {
            return Error.Conflict(ErrorCodes.Expense.CategoryInactive, "This category is not in use any more.");
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

        if (!account.IsActive)
        {
            return Error.Conflict(ErrorCodes.Expense.AccountInactive, "This account is not in use any more.");
        }

        if (r.SupplierId is { } supplierId && !await db.Suppliers.AnyAsync(s => s.Id == supplierId, cancellationToken))
        {
            return Error.NotFound(ErrorCodes.Purchase.SupplierNotFound, "Supplier not found.");
        }

        return new ExpenseDetails(r.CategoryId, r.ExpenseDate, Math.Round(r.Amount, 2, MidpointRounding.AwayFromZero), r.Method, r.AccountId, r.SupplierId, r.Reference, r.Notes);
    }

    public async Task<Result<ExpenseDetailDto>> CreateAsync(SaveExpenseRequest request, CancellationToken cancellationToken)
    {
        var target = await access.TargetAsync(request.BranchId, Permissions.Accounting.ExpenseCreate, cancellationToken);
        if (target.IsFailure)
        {
            return target.Error!;
        }

        var details = await ValidateAsync(target.Value.Branch.Id, request, cancellationToken);
        if (details.IsFailure)
        {
            return details.Error!;
        }

        var files = await attachments.ResolveAsync(Folder, request.AttachmentPaths, ErrorCodes.Expense.AttachmentInvalid, 10, cancellationToken);
        if (files.IsFailure)
        {
            return files.Error!;
        }

        return await uow.ExecuteInTransactionAsync<Result<ExpenseDetailDto>>(async ct =>
        {
            var expense = await core.CreateAsync(target.Value.Branch, details.Value, null, null, ct);
            foreach (var f in files.Value)
            {
                expense.AddAttachment(f.Path, f.FileName, f.ContentType, f.Size);
            }

            await db.SaveChangesAsync(ct);
            return await DetailAsync(expense, ct);
        }, cancellationToken);
    }

    public async Task<Result<ExpenseDetailDto>> UpdateAsync(Guid id, SaveExpenseRequest request, CancellationToken cancellationToken)
    {
        var actor = await access.ActorAsync(cancellationToken);
        if (actor.IsFailure)
        {
            return actor.Error!;
        }

        var expense = await LoadAsync(id, cancellationToken);
        if (expense is null)
        {
            return NotFound();
        }

        if (await CheckAsync(actor.Value, expense.BranchId, Permissions.Accounting.ExpenseEdit, cancellationToken) is { } refusal)
        {
            return refusal;
        }

        if (expense.Status != ExpenseStatus.Draft)
        {
            return Error.Conflict(ErrorCodes.Expense.NotEditable, $"Only a draft expense can be edited; this one is {expense.Status}.");
        }

        var details = await ValidateAsync(expense.BranchId, request, cancellationToken);
        if (details.IsFailure)
        {
            return details.Error!;
        }

        var files = await attachments.ResolveAsync(Folder, request.AttachmentPaths, ErrorCodes.Expense.AttachmentInvalid, 10, cancellationToken);
        if (files.IsFailure)
        {
            return files.Error!;
        }

        expense.Update(details.Value);
        foreach (var f in files.Value.Where(f => expense.Attachments.All(a => a.Path != f.Path)))
        {
            expense.AddAttachment(f.Path, f.FileName, f.ContentType, f.Size);
        }

        await db.SaveChangesAsync(cancellationToken);
        return await DetailAsync(expense, cancellationToken);
    }

    public async Task<Result> DeleteAsync(Guid id, CancellationToken cancellationToken)
    {
        var actor = await access.ActorAsync(cancellationToken);
        if (actor.IsFailure)
        {
            return actor.Error!;
        }

        var expense = await LoadAsync(id, cancellationToken);
        if (expense is null)
        {
            return NotFound();
        }

        if (await CheckAsync(actor.Value, expense.BranchId, Permissions.Accounting.ExpenseDelete, cancellationToken) is { } refusal)
        {
            return refusal;
        }

        if (expense.Status != ExpenseStatus.Draft)
        {
            return Error.Conflict(ErrorCodes.Expense.NotEditable, $"Only a draft expense can be removed; this one is {expense.Status}.");
        }

        expense.MarkDeleted(clock.GetUtcNow(), actor.Value.Id);
        await db.SaveChangesAsync(cancellationToken);
        return Result.Success();
    }

    public async Task<Result<AttachmentUploadDto>> UploadAsync(Stream content, string? fileName, CancellationToken cancellationToken)
    {
        var saved = await attachments.SaveAsync(Folder, content, fileName, ErrorCodes.Expense.AttachmentInvalid, cancellationToken);
        return saved.IsFailure ? saved.Error! : new AttachmentUploadDto(saved.Value.Path, saved.Value.FileName, saved.Value.ContentType, saved.Value.Size, saved.Value.Url);
    }

    private async Task<Result<ExpenseDetailDto>> RunAsync(Guid id, string permission, Func<Expense, FinanceAccount, Ums.Actor, Who, CancellationToken, Task<Result>> action, CancellationToken cancellationToken)
    {
        var actor = await access.ActorAsync(cancellationToken);
        if (actor.IsFailure)
        {
            return actor.Error!;
        }

        return await uow.ExecuteInTransactionAsync<Result<ExpenseDetailDto>>(async ct =>
        {
            var expense = await LoadAsync(id, ct);
            if (expense is null)
            {
                return NotFound();
            }

            if (await CheckAsync(actor.Value, expense.BranchId, permission, ct) is { } refusal)
            {
                return refusal;
            }

            var account = await db.FinanceAccounts.FirstAsync(a => a.Id == expense.AccountId, ct);
            var done = await action(expense, account, actor.Value, orderAccess.WhoIs(actor.Value), ct);
            if (done.IsFailure)
            {
                return done.Error!;
            }

            await db.SaveChangesAsync(ct);
            return await DetailAsync(expense, ct);
        }, cancellationToken);
    }

    private static Result Guard(Expense e, ExpenseStatus to) =>
        ExpenseStateMachine.CanMove(e.Status, to) ? Result.Success() : Error.Conflict(ErrorCodes.Expense.InvalidTransition, $"An expense that is {e.Status} cannot become {to}.");

    public Task<Result<ExpenseDetailDto>> SubmitAsync(Guid id, CancellationToken cancellationToken) =>
        RunAsync(id, Permissions.Accounting.ExpenseCreate, async (e, account, _, who, ct) =>
        {
            if (Guard(e, ExpenseStatus.Submitted) is { IsFailure: true } refused)
            {
                return refused;
            }

            if (!account.IsActive)
            {
                return Error.Conflict(ErrorCodes.Expense.AccountInactive, "This account is not in use any more.");
            }

            await core.SubmitAsync(e, account, who, ct);
            return Result.Success();
        }, cancellationToken);

    public Task<Result<ExpenseDetailDto>> ApproveAsync(Guid id, DecideExpenseRequest request, CancellationToken cancellationToken) =>
        Ops.TooLong(request.Note, 500)
            ? Task.FromResult<Result<ExpenseDetailDto>>(Ops.Invalid("note", "The note can have at most 500 characters."))
            : RunAsync(id, Permissions.Accounting.ExpenseApprove, (e, account, actor, who, _) =>
            {
                if (Guard(e, ExpenseStatus.Approved) is { IsFailure: true } refused)
                {
                    return Task.FromResult(refused);
                }

                if (e.SubmittedBy == who.Id && !actor.IsSuperAdmin)
                {
                    return Task.FromResult<Result>(Error.Forbidden(ErrorCodes.Expense.SelfApproval, "You cannot approve an expense you submitted yourself."));
                }

                core.Approve(e, account, who, request.Note, auto: false);
                return Task.FromResult(Result.Success());
            }, cancellationToken);

    public async Task<Result<ExpenseDetailDto>> RejectAsync(Guid id, RejectExpenseRequest request, CancellationToken cancellationToken)
    {
        var reason = request.Reason?.Trim();
        if (string.IsNullOrEmpty(reason) || reason.Length > 500)
        {
            return Ops.Invalid("reason", "Give the reason for rejecting the expense (up to 500 characters).");
        }

        return await RunAsync(id, Permissions.Accounting.ExpenseApprove, (e, _, _, who, _) =>
        {
            if (Guard(e, ExpenseStatus.Rejected) is { IsFailure: true } refused)
            {
                return Task.FromResult(refused);
            }

            e.Reject(clock.GetUtcNow(), who.Id, who.Name, reason);
            return Task.FromResult(Result.Success());
        }, cancellationToken);
    }

    /// <summary>Takes a submitted expense back to Draft (the author) or a rejected one (to fix it and send it again).</summary>
    public Task<Result<ExpenseDetailDto>> ReturnToDraftAsync(Guid id, CancellationToken cancellationToken) =>
        RunAsync(id, Permissions.Accounting.ExpenseCreate, (e, _, _, _, _) =>
        {
            if (Guard(e, ExpenseStatus.Draft) is { IsFailure: true } refused)
            {
                return Task.FromResult(refused);
            }

            e.ReturnToDraft();
            return Task.FromResult(Result.Success());
        }, cancellationToken);

    internal static string Period(DateOnly date) => date.ToString("yyMM", CultureInfo.InvariantCulture);
}
