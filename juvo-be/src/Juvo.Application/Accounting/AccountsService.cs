using Juvo.Application.Abstractions;
using Juvo.Application.Inventory;
using Juvo.Application.Operations;
using Juvo.Application.Orders;
using Juvo.Application.Ums;
using Juvo.Domain.Accounting;
using Juvo.Domain.Payments;
using Juvo.Domain.Couriers;
using Juvo.Shared.Pagination;
using Juvo.Shared.Results;
using Juvo.Shared.Security;
using Microsoft.EntityFrameworkCore;

namespace Juvo.Application.Accounting;

/// <summary>Adds lines to the cash book. Every caller saves in its own transaction; the unique index on source and kind makes a double booking impossible.</summary>
public sealed class LedgerWriter(IApplicationDbContext db)
{
    public LedgerEntry Add(FinanceAccount account, DateOnly date, LedgerEntryKind kind, decimal amount, string description, string sourceType, Guid sourceId, Guid? transferId, string? actorName)
    {
        var entry = new LedgerEntry(account.BranchId, account.Id, date, kind, amount, description, sourceType, sourceId, transferId, actorName);
        db.LedgerEntries.Add(entry);
        return entry;
    }
}

/// <summary>
/// Cash and bank accounts, transfers, adjustments, balances and the day book (module X). A balance is the opening balance plus the signed ledger entries up to a date. The day book
/// lists those entries and, read from the payment data, customer payments, settled COD cash and completed refunds (see docs/operations-api.md). Accounts belong to one branch; reads are
/// limited to the branches where the actor holds <c>accounting.cashbook.view</c>.
/// </summary>
public sealed class AccountsService(IApplicationDbContext db, InventoryAccess access, LedgerWriter ledger, OrderAccess orderAccess, TimeProvider clock)
{
    private static Error AccountNotFound() => Error.NotFound(ErrorCodes.Expense.AccountNotFound, "Account not found.");

    // ---- Accounts ----------------------------------------------------------------------------------------

    public async Task<Result<IReadOnlyList<AccountDto>>> ListAsync(Guid? branchId, bool includeInactive, CancellationToken cancellationToken)
    {
        var actor = await access.ActorAsync(cancellationToken);
        if (actor.IsFailure)
        {
            return actor.Error!;
        }

        var visible = InventoryAccess.VisibleBranches(actor.Value, Permissions.Accounting.CashbookView);
        var query = from a in db.FinanceAccounts.AsNoTracking() join b in db.Branches.AsNoTracking() on a.BranchId equals b.Id select new { Account = a, BranchCode = b.Code };
        if (visible is not null)
        {
            query = query.Where(x => visible.Contains(x.Account.BranchId));
        }

        if (branchId is { } id)
        {
            query = query.Where(x => x.Account.BranchId == id);
        }

        if (!includeInactive)
        {
            query = query.Where(x => x.Account.IsActive);
        }

        var rows = await query.OrderBy(x => x.BranchCode).ThenBy(x => x.Account.Name).ToListAsync(cancellationToken);
        var ids = rows.Select(r => r.Account.Id).ToList();
        var sums = await db.LedgerEntries.AsNoTracking().Where(e => ids.Contains(e.AccountId)).GroupBy(e => e.AccountId).Select(g => new { g.Key, Sum = g.Sum(e => e.Amount) }).ToDictionaryAsync(x => x.Key, x => x.Sum, cancellationToken);
        return rows.Select(r => ToDto(r.Account, r.BranchCode, sums.GetValueOrDefault(r.Account.Id))).ToList();
    }

    private static AccountDto ToDto(FinanceAccount a, string branchCode, decimal entries) =>
        new(a.Id, a.BranchId, branchCode, a.Name, a.Type, a.AccountNumber, a.Note, a.OpeningBalance, a.OpeningDate, a.IsActive, a.OpeningBalance + entries);

    public async Task<Result<AccountDto>> CreateAsync(SaveAccountRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Name) || Ops.TooLong(request.Name, 120) || !Enum.IsDefined(request.Type) || Ops.TooLong(request.AccountNumber, 60) || Ops.TooLong(request.Note, 300))
        {
            return Ops.Invalid("name", "Give the account a name (up to 120 characters) and a type.");
        }

        var target = await access.TargetAsync(request.BranchId, Permissions.Accounting.CashbookEdit, cancellationToken);
        if (target.IsFailure)
        {
            return target.Error!;
        }

        var branch = target.Value.Branch;
        var name = request.Name.Trim();
        if (await db.FinanceAccounts.AnyAsync(a => a.BranchId == branch.Id && a.Name == name, cancellationToken))
        {
            return Error.Conflict(ErrorCodes.General.Conflict, "This branch already has an account with that name.");
        }

        var today = Ops.LocalDate(branch, clock.GetUtcNow());
        var account = new FinanceAccount(branch.Id, name, request.Type, request.OpeningBalance, request.OpeningDate ?? today, request.AccountNumber, request.Note);
        db.FinanceAccounts.Add(account);
        await db.SaveChangesAsync(cancellationToken);
        return ToDto(account, branch.Code, 0m);
    }

    public async Task<Result<AccountDto>> UpdateAsync(Guid id, UpdateAccountRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Name) || Ops.TooLong(request.Name, 120) || !Enum.IsDefined(request.Type) || Ops.TooLong(request.AccountNumber, 60) || Ops.TooLong(request.Note, 300))
        {
            return Ops.Invalid("name", "Give the account a name (up to 120 characters) and a type.");
        }

        var loaded = await LoadAsync(id, Permissions.Accounting.CashbookEdit, cancellationToken);
        if (loaded.IsFailure)
        {
            return loaded.Error!;
        }

        var (account, branchCode) = loaded.Value;
        var name = request.Name.Trim();
        if (await db.FinanceAccounts.AnyAsync(a => a.BranchId == account.BranchId && a.Name == name && a.Id != id, cancellationToken))
        {
            return Error.Conflict(ErrorCodes.General.Conflict, "This branch already has an account with that name.");
        }

        account.Update(name, request.Type, request.AccountNumber, request.Note);
        account.SetActive(request.IsActive);
        await db.SaveChangesAsync(cancellationToken);
        var sum = await db.LedgerEntries.AsNoTracking().Where(e => e.AccountId == id).SumAsync(e => (decimal?)e.Amount, cancellationToken) ?? 0m;
        return ToDto(account, branchCode, sum);
    }

    private async Task<Result<(FinanceAccount Account, string BranchCode)>> LoadAsync(Guid id, string permission, CancellationToken cancellationToken)
    {
        var actor = await access.ActorAsync(cancellationToken);
        if (actor.IsFailure)
        {
            return actor.Error!;
        }

        var account = await db.FinanceAccounts.FirstOrDefaultAsync(a => a.Id == id, cancellationToken);
        if (account is null)
        {
            return AccountNotFound();
        }

        var refusal = await access.CheckAsync(actor.Value, account.BranchId, permission, cancellationToken);
        if (refusal is { Type: ErrorType.NotFound })
        {
            return AccountNotFound();
        }

        if (refusal is not null)
        {
            return refusal;
        }

        var code = await db.Branches.AsNoTracking().Where(b => b.Id == account.BranchId).Select(b => b.Code).FirstAsync(cancellationToken);
        return (account, code);
    }

    // ---- Transfers and adjustments --------------------------------------------------------------------------

    public async Task<Result<TransferResultDto>> TransferAsync(TransferRequest request, CancellationToken cancellationToken)
    {
        if (request.FromAccountId == request.ToAccountId || request.Amount <= 0 || Ops.TooLong(request.Note, 200))
        {
            return Error.Failure(ErrorCodes.Expense.TransferInvalid, "A transfer needs two different accounts and a positive amount.");
        }

        var from = await LoadAsync(request.FromAccountId, Permissions.Accounting.CashbookEdit, cancellationToken);
        if (from.IsFailure)
        {
            return from.Error!;
        }

        var to = await LoadAsync(request.ToAccountId, Permissions.Accounting.CashbookEdit, cancellationToken);
        if (to.IsFailure)
        {
            return to.Error!;
        }

        if (!from.Value.Account.IsActive || !to.Value.Account.IsActive)
        {
            return Error.Conflict(ErrorCodes.Expense.AccountInactive, "An inactive account cannot take part in a transfer.");
        }

        var who = await WhoAsync(cancellationToken);
        var date = request.Date ?? Ops.BstDate(clock.GetUtcNow());
        var transferId = Guid.CreateVersion7();
        var note = string.IsNullOrWhiteSpace(request.Note) ? string.Empty : $": {request.Note.Trim()}";
        var amount = Math.Round(request.Amount, 2, MidpointRounding.AwayFromZero);
        var outEntry = ledger.Add(from.Value.Account, date, LedgerEntryKind.TransferOut, -amount, $"Transfer to {to.Value.Account.Name}{note}", "Transfer", transferId, transferId, who);
        var inEntry = ledger.Add(to.Value.Account, date, LedgerEntryKind.TransferIn, amount, $"Transfer from {from.Value.Account.Name}{note}", "Transfer", transferId, transferId, who);
        await db.SaveChangesAsync(cancellationToken);
        return new TransferResultDto(transferId, Entry(outEntry, from.Value.Account.Name), Entry(inEntry, to.Value.Account.Name));
    }

    public async Task<Result<LedgerEntryDto>> AdjustAsync(AdjustmentRequest request, CancellationToken cancellationToken)
    {
        if (request.Amount == 0 || string.IsNullOrWhiteSpace(request.Reason) || Ops.TooLong(request.Reason, 250))
        {
            return Ops.Invalid("reason", "Give a non-zero amount and the reason for the correction (up to 250 characters).");
        }

        var loaded = await LoadAsync(request.AccountId, Permissions.Accounting.CashbookEdit, cancellationToken);
        if (loaded.IsFailure)
        {
            return loaded.Error!;
        }

        var account = loaded.Value.Account;
        var entry = ledger.Add(account, request.Date ?? Ops.BstDate(clock.GetUtcNow()), LedgerEntryKind.Adjustment, Math.Round(request.Amount, 2, MidpointRounding.AwayFromZero),
            $"Adjustment: {request.Reason.Trim()}", "Adjustment", Guid.CreateVersion7(), null, await WhoAsync(cancellationToken));
        await db.SaveChangesAsync(cancellationToken);
        return Entry(entry, account.Name);
    }

    public async Task<Result<PagedResult<LedgerEntryDto>>> EntriesAsync(Guid accountId, PageRequest request, CancellationToken cancellationToken)
    {
        var loaded = await LoadAsync(accountId, Permissions.Accounting.CashbookView, cancellationToken);
        if (loaded.IsFailure)
        {
            return loaded.Error!;
        }

        var name = loaded.Value.Account.Name;
        var query = db.LedgerEntries.AsNoTracking().Where(e => e.AccountId == accountId).OrderByDescending(e => e.EntryDate).ThenByDescending(e => e.CreatedAt);
        var page = request.Normalized;
        var total = await query.CountAsync(cancellationToken);
        var rows = await query.Skip(page.Skip).Take(page.PageSize).ToListAsync(cancellationToken);
        return new PagedResult<LedgerEntryDto>([.. rows.Select(e => Entry(e, name))], total, page.Page, page.PageSize);
    }

    private static LedgerEntryDto Entry(LedgerEntry e, string accountName) =>
        new(e.Id, e.AccountId, accountName, e.EntryDate, e.Kind, e.Amount, e.Description, e.SourceType, e.SourceId, e.TransferId, e.ActorName);

    private async Task<string?> WhoAsync(CancellationToken cancellationToken) =>
        await orderAccess.ActorAsync(cancellationToken) is { } actor ? orderAccess.WhoIs(actor).Name : null;

    // ---- Balances -------------------------------------------------------------------------------------------

    public async Task<Result<AccountBalancesDto>> BalancesAsync(DateOnly? asOf, Guid? branchId, CancellationToken cancellationToken)
    {
        var actor = await access.ActorAsync(cancellationToken);
        if (actor.IsFailure)
        {
            return actor.Error!;
        }

        var date = asOf ?? Ops.BstDate(clock.GetUtcNow());
        var visible = InventoryAccess.VisibleBranches(actor.Value, Permissions.Accounting.CashbookView);
        var accounts = from a in db.FinanceAccounts.AsNoTracking() join b in db.Branches.AsNoTracking() on a.BranchId equals b.Id select new { Account = a, BranchCode = b.Code };
        if (visible is not null)
        {
            accounts = accounts.Where(x => visible.Contains(x.Account.BranchId));
        }

        if (branchId is { } id)
        {
            accounts = accounts.Where(x => x.Account.BranchId == id);
        }

        var rows = await accounts.OrderBy(x => x.BranchCode).ThenBy(x => x.Account.Name).ToListAsync(cancellationToken);
        var ids = rows.Select(r => r.Account.Id).ToList();
        var sums = await db.LedgerEntries.AsNoTracking().Where(e => ids.Contains(e.AccountId) && e.EntryDate <= date).GroupBy(e => e.AccountId)
            .Select(g => new { g.Key, In = g.Sum(e => e.Amount > 0 ? e.Amount : 0m), Out = g.Sum(e => e.Amount < 0 ? -e.Amount : 0m) }).ToDictionaryAsync(x => x.Key, cancellationToken);
        var list = rows.Select(r =>
        {
            var s = sums.GetValueOrDefault(r.Account.Id);
            return new AccountBalanceDto(r.Account.Id, r.Account.Name, r.Account.Type, r.Account.BranchId, r.BranchCode, r.Account.IsActive, r.Account.OpeningBalance, s?.In ?? 0m, s?.Out ?? 0m,
                r.Account.OpeningBalance + (s?.In ?? 0m) - (s?.Out ?? 0m));
        }).ToList();

        var derived = await DerivedTotalsAsync(DateOnly.MinValue, date, visible, branchId, cancellationToken);
        return new AccountBalancesDto(date, list, list.Sum(a => a.Balance), derived.In - derived.Out);
    }

    // ---- Day book -------------------------------------------------------------------------------------------

    private sealed record DerivedRow(DateTimeOffset At, string Kind, decimal Amount, string Description, Guid BranchId, string? SourceType, Guid SourceId);

    /// <summary>One source of money that is read from the payment data: its filtered query, and how to sum, count and page it.</summary>
    private sealed class DerivedPart<T>(IQueryable<T> query, System.Linq.Expressions.Expression<Func<T, decimal?>> amount, System.Linq.Expressions.Expression<Func<T, DateTimeOffset>> at, System.Linq.Expressions.Expression<Func<T, DerivedRow>> row)
        : IDerivedPart
    {
        public async Task<decimal> SumAsync(CancellationToken ct) => await query.SumAsync(amount, ct) ?? 0m;

        public Task<int> CountAsync(CancellationToken ct) => query.CountAsync(ct);

        public Task<List<DerivedRow>> TopAsync(int take, CancellationToken ct) => query.OrderByDescending(at).Take(take).Select(row).ToListAsync(ct);
    }

    private interface IDerivedPart
    {
        Task<decimal> SumAsync(CancellationToken ct);

        Task<int> CountAsync(CancellationToken ct);

        Task<List<DerivedRow>> TopAsync(int take, CancellationToken ct);
    }

    private static readonly Domain.Orders.PaymentStatus[] PaidStatuses = [Domain.Orders.PaymentStatus.Paid, Domain.Orders.PaymentStatus.PartiallyRefunded, Domain.Orders.PaymentStatus.Refunded];

    private DerivedPart<Payment> CustomerReceipts(DateTimeOffset from, DateTimeOffset to, List<Guid>? visible, Guid? branchId)
    {
        var q = db.Payments.AsNoTracking().Where(p => p.Kind != PaymentKind.Cod && p.Kind != PaymentKind.StoreCredit && p.PaidAt != null && p.PaidAt >= from && p.PaidAt < to && PaidStatuses.Contains(p.Status));
        if (visible is not null)
        {
            q = q.Where(p => visible.Contains(p.BranchId));
        }

        if (branchId is { } id)
        {
            q = q.Where(p => p.BranchId == id);
        }

        return new DerivedPart<Payment>(q, p => p.Amount, p => p.PaidAt!.Value,
            p => new DerivedRow(p.PaidAt!.Value, "CustomerReceipt", p.Amount, "Payment for order " + p.OrderNumber, p.BranchId, "Payment", p.Id));
    }

    private DerivedPart<CodSettlement> CodReceipts(DateTimeOffset from, DateTimeOffset to, List<Guid>? visible, Guid? branchId)
    {
        var q = db.CodSettlements.AsNoTracking().Where(s => s.Status == SettlementStatus.Settled && s.SettledAt != null && s.SettledAt >= from && s.SettledAt < to);
        if (visible is not null)
        {
            q = q.Where(s => visible.Contains(s.BranchId));
        }

        if (branchId is { } id)
        {
            q = q.Where(s => s.BranchId == id);
        }

        return new DerivedPart<CodSettlement>(q, s => s.ReceivedTotal ?? 0m, s => s.SettledAt!.Value,
            s => new DerivedRow(s.SettledAt!.Value, "CodSettlement", s.ReceivedTotal ?? 0m, "COD settlement " + s.Number + " (" + s.CourierName + ")", s.BranchId, "CodSettlement", s.Id));
    }

    private DerivedPart<Refund> Refunded(DateTimeOffset from, DateTimeOffset to, List<Guid>? visible, Guid? branchId)
    {
        var q = db.Refunds.AsNoTracking().Where(r => r.Status == RefundState.Completed && r.Mode != RefundMode.StoreCredit && r.CompletedAt != null && r.CompletedAt >= from && r.CompletedAt < to);
        if (visible is not null)
        {
            q = q.Where(r => visible.Contains(r.BranchId));
        }

        if (branchId is { } id)
        {
            q = q.Where(r => r.BranchId == id);
        }

        var payments = db.Payments.AsNoTracking();
        return new DerivedPart<Refund>(q, r => -r.Amount, r => r.CompletedAt!.Value,
            r => new DerivedRow(r.CompletedAt!.Value, "Refund", -r.Amount, "Refund for order " + payments.Where(p => p.Id == r.PaymentId).Select(p => p.OrderNumber).FirstOrDefault(), r.BranchId, "Refund", r.Id));
    }

    private async Task<(decimal In, decimal Out)> DerivedTotalsAsync(DateOnly from, DateOnly to, List<Guid>? visible, Guid? branchId, CancellationToken cancellationToken)
    {
        var fromAt = from == DateOnly.MinValue ? new DateTimeOffset(2000, 1, 1, 0, 0, 0, Ops.Bst) : Ops.StartOfDay(from);
        var toAt = Ops.StartOfDay(to).AddDays(1);
        var receipts = await CustomerReceipts(fromAt, toAt, visible, branchId).SumAsync(cancellationToken);
        var cod = await CodReceipts(fromAt, toAt, visible, branchId).SumAsync(cancellationToken);
        var refunds = await Refunded(fromAt, toAt, visible, branchId).SumAsync(cancellationToken); // negative
        return (receipts + cod, -refunds);
    }

    public async Task<Result<DaybookDto>> DaybookAsync(PageRequest request, DaybookQuery filter, CancellationToken cancellationToken)
    {
        var actor = await access.ActorAsync(cancellationToken);
        if (actor.IsFailure)
        {
            return actor.Error!;
        }

        var to = filter.To ?? Ops.BstDate(clock.GetUtcNow());
        var from = filter.From ?? to.AddDays(-30);
        if (from > to)
        {
            return Ops.Invalid("from", "The start date must not be after the end date.");
        }

        var visible = InventoryAccess.VisibleBranches(actor.Value, Permissions.Accounting.CashbookView);
        var stored = db.LedgerEntries.AsNoTracking().Where(e => e.EntryDate >= from && e.EntryDate <= to);
        if (visible is not null)
        {
            stored = stored.Where(e => visible.Contains(e.BranchId));
        }

        if (filter.BranchId is { } branchId)
        {
            stored = stored.Where(e => e.BranchId == branchId);
        }

        if (filter.AccountId is { } accountId)
        {
            stored = stored.Where(e => e.AccountId == accountId);
        }

        var fromAt = Ops.StartOfDay(from);
        var toAt = Ops.StartOfDay(to).AddDays(1);
        var includeDerived = filter.AccountId is null; // money read from the payment data belongs to no account
        var page = request.Normalized;
        var take = page.Skip + page.PageSize;

        var storedCount = await stored.CountAsync(cancellationToken);
        var storedRows = await stored.OrderByDescending(e => e.EntryDate).ThenByDescending(e => e.CreatedAt).Take(take).ToListAsync(cancellationToken);
        var derivedRows = new List<DerivedRow>();
        var derivedCount = 0;
        if (includeDerived)
        {
            var parts = new IDerivedPart[] { CustomerReceipts(fromAt, toAt, visible, filter.BranchId), CodReceipts(fromAt, toAt, visible, filter.BranchId), Refunded(fromAt, toAt, visible, filter.BranchId) };
            foreach (var part in parts)
            {
                derivedCount += await part.CountAsync(cancellationToken);
                derivedRows.AddRange(await part.TopAsync(take, cancellationToken));
            }
        }

        var accountIds = storedRows.Select(e => e.AccountId).Distinct().ToList();
        var accounts = await db.FinanceAccounts.AsNoTracking().Where(a => accountIds.Contains(a.Id)).ToDictionaryAsync(a => a.Id, a => a.Name, cancellationToken);
        var branches = await db.Branches.AsNoTracking().ToDictionaryAsync(b => b.Id, b => b.Code, cancellationToken);
        var merged = storedRows.Select(e => new DaybookEntryDto(e.EntryDate, e.CreatedAt, e.Kind.ToString(), e.Amount, e.Description, e.BranchId, branches.GetValueOrDefault(e.BranchId, string.Empty), e.AccountId,
                accounts.GetValueOrDefault(e.AccountId), e.SourceType, e.SourceId, false))
            .Concat(derivedRows.Select(r => new DaybookEntryDto(Ops.BstDate(r.At), r.At, r.Kind, r.Amount, r.Description, r.BranchId, branches.GetValueOrDefault(r.BranchId, string.Empty), null, null, r.SourceType, r.SourceId, true)))
            .OrderByDescending(e => e.Date).ThenByDescending(e => e.At).Skip(page.Skip).Take(page.PageSize).ToList();

        var storedIn = await stored.SumAsync(e => (decimal?)(e.Amount > 0 ? e.Amount : 0m), cancellationToken) ?? 0m;
        var storedOut = await stored.SumAsync(e => (decimal?)(e.Amount < 0 ? -e.Amount : 0m), cancellationToken) ?? 0m;
        var (derivedIn, derivedOut) = includeDerived ? await DerivedTotalsAsync(from, to, visible, filter.BranchId, cancellationToken) : (0m, 0m);
        var totalIn = storedIn + derivedIn;
        var totalOut = storedOut + derivedOut;
        return new DaybookDto(new PagedResult<DaybookEntryDto>(merged, storedCount + derivedCount, page.Page, page.PageSize), totalIn, totalOut, totalIn - totalOut, from, to);
    }
}
