using Juvo.Domain.Common;

namespace Juvo.Domain.Accounting;

/// <summary>How money was moved. Shared by expenses, supplier payments and the cash book. Stored as an integer; append new values at the end.</summary>
public enum MoneyMethod
{
    Cash = 1,
    Bank = 2,
    MobileBanking = 3,
    Cheque = 4,
    Other = 5,
}

public enum AccountType
{
    Cash = 1,
    Bank = 2,
    MobileBanking = 3,
}

/// <summary>
/// A simple cash or bank account of one branch (a drawer, a bank account, a bKash wallet). The balance is the opening balance plus the
/// signed amounts of its <see cref="LedgerEntry"/> rows; nothing else is stored, so it can always be explained.
/// </summary>
public sealed class FinanceAccount : Entity, IBranchScoped
{
    private FinanceAccount()
    {
    }

    public FinanceAccount(Guid branchId, string name, AccountType type, decimal openingBalance, DateOnly openingDate, string? accountNumber, string? note)
    {
        BranchId = branchId;
        Update(name, type, accountNumber, note);
        OpeningBalance = openingBalance;
        OpeningDate = openingDate;
    }

    public Guid BranchId { get; private set; }

    public string Name { get; private set; } = string.Empty;

    public AccountType Type { get; private set; }

    public string? AccountNumber { get; private set; }

    public string? Note { get; private set; }

    public decimal OpeningBalance { get; private set; }

    /// <summary>The day the opening balance was true (informational: the balance counts every entry up to the date asked for).</summary>
    public DateOnly OpeningDate { get; private set; }

    public bool IsActive { get; private set; } = true;

    public void Update(string name, AccountType type, string? accountNumber, string? note)
    {
        Name = name.Trim();
        Type = type;
        AccountNumber = string.IsNullOrWhiteSpace(accountNumber) ? null : accountNumber.Trim();
        Note = string.IsNullOrWhiteSpace(note) ? null : note.Trim();
    }

    public void SetActive(bool active) => IsActive = active;
}

public enum LedgerEntryKind
{
    /// <summary>Money paid out for an approved expense (negative).</summary>
    Expense = 1,

    /// <summary>Money paid to a supplier against a purchase order (negative).</summary>
    SupplierPayment = 2,

    /// <summary>Money moved out of this account to another account (negative).</summary>
    TransferOut = 3,

    /// <summary>Money moved into this account from another account (positive).</summary>
    TransferIn = 4,

    /// <summary>A correction with a reason (positive or negative).</summary>
    Adjustment = 5,
}

/// <summary>
/// One line of the cash book: append-only, never edited or deleted. The amount is signed (positive = money in, negative = money out).
/// Entries are produced by approved expenses, supplier payments, transfers and adjustments; customer receipts, COD settlements and refunds are read from the payment
/// data when the day book is built and are never copied here (docs/operations-api.md, section Accounts).
/// </summary>
public sealed class LedgerEntry : Entity, IBranchScoped, IAppendOnly, INotAudited
{
    private LedgerEntry()
    {
    }

    public LedgerEntry(Guid branchId, Guid accountId, DateOnly entryDate, LedgerEntryKind kind, decimal amount, string description, string? sourceType, Guid? sourceId, Guid? transferId, string? actorName)
    {
        if (amount == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(amount), "A ledger entry needs a non-zero amount.");
        }

        BranchId = branchId;
        AccountId = accountId;
        EntryDate = entryDate;
        Kind = kind;
        Amount = amount;
        Description = description.Trim();
        SourceType = sourceType;
        SourceId = sourceId;
        TransferId = transferId;
        ActorName = actorName;
    }

    public Guid BranchId { get; private set; }

    public Guid AccountId { get; private set; }

    /// <summary>The business date (branch local time).</summary>
    public DateOnly EntryDate { get; private set; }

    public LedgerEntryKind Kind { get; private set; }

    public decimal Amount { get; private set; }

    public string Description { get; private set; } = string.Empty;

    /// <summary><c>Expense</c>, <c>PurchasePayment</c>, <c>Transfer</c> or <c>Adjustment</c>.</summary>
    public string? SourceType { get; private set; }

    public Guid? SourceId { get; private set; }

    /// <summary>Both sides of a transfer share this id.</summary>
    public Guid? TransferId { get; private set; }

    public string? ActorName { get; private set; }
}

public enum ExpenseStatus
{
    Draft = 1,
    Submitted = 2,
    Approved = 3,
    Rejected = 4,
}

/// <summary>Draft to Submitted; Submitted to Approved, Rejected or back to Draft (withdrawn); Rejected back to Draft (to fix and send again). Approved is final.</summary>
public static class ExpenseStateMachine
{
    private static readonly Dictionary<ExpenseStatus, ExpenseStatus[]> Moves = new()
    {
        [ExpenseStatus.Draft] = [ExpenseStatus.Submitted],
        [ExpenseStatus.Submitted] = [ExpenseStatus.Approved, ExpenseStatus.Rejected, ExpenseStatus.Draft],
        [ExpenseStatus.Approved] = [],
        [ExpenseStatus.Rejected] = [ExpenseStatus.Draft],
    };

    public static IReadOnlyList<ExpenseStatus> NextFor(ExpenseStatus from) => Moves[from];

    public static bool CanMove(ExpenseStatus from, ExpenseStatus to) => Moves[from].Contains(to);
}

public sealed class ExpenseCategory : Entity
{
    private ExpenseCategory()
    {
    }

    public ExpenseCategory(string nameEn, string? nameBn, Guid? parentId)
    {
        Update(nameEn, nameBn, parentId);
    }

    public string NameEn { get; private set; } = string.Empty;

    public string? NameBn { get; private set; }

    public Guid? ParentId { get; private set; }

    public bool IsActive { get; private set; } = true;

    public void Update(string nameEn, string? nameBn, Guid? parentId)
    {
        NameEn = nameEn.Trim();
        NameBn = string.IsNullOrWhiteSpace(nameBn) ? null : nameBn.Trim();
        ParentId = parentId;
    }

    public void SetActive(bool active) => IsActive = active;
}

/// <summary>The fields staff can change on an expense.</summary>
public sealed record ExpenseDetails(Guid CategoryId, DateOnly ExpenseDate, decimal Amount, MoneyMethod Method, Guid AccountId, Guid? SupplierId, string? Reference, string? Notes);

/// <summary>
/// An expense of one branch (module X): Draft, Submitted, then Approved or Rejected. Approval books it into the cash book (an outflow from its account).
/// Expenses are archived, never erased, and only a draft can be removed.
/// </summary>
public sealed class Expense : SoftDeletableEntity, IBranchScoped
{
    private readonly List<ExpenseAttachment> _attachments = [];

    private Expense()
    {
    }

    public Expense(string number, Guid branchId, ExpenseDetails details, Guid? recurringExpenseId, string? recurringPeriod)
    {
        Number = number;
        BranchId = branchId;
        Apply(details);
        RecurringExpenseId = recurringExpenseId;
        RecurringPeriod = recurringPeriod;
    }

    public string Number { get; private set; } = string.Empty;

    public Guid BranchId { get; private set; }

    public Guid CategoryId { get; private set; }

    public DateOnly ExpenseDate { get; private set; }

    public decimal Amount { get; private set; }

    public MoneyMethod Method { get; private set; }

    public Guid AccountId { get; private set; }

    public Guid? SupplierId { get; private set; }

    public string? Reference { get; private set; }

    public string? Notes { get; private set; }

    public ExpenseStatus Status { get; private set; } = ExpenseStatus.Draft;

    public DateTimeOffset? SubmittedAt { get; private set; }

    public Guid? SubmittedBy { get; private set; }

    public DateTimeOffset? DecidedAt { get; private set; }

    public Guid? DecidedBy { get; private set; }

    public string? DecidedByName { get; private set; }

    /// <summary>True when the amount was at or below the approval threshold, so no person had to approve it.</summary>
    public bool AutoApproved { get; private set; }

    /// <summary>Why it was rejected, or the approver's note.</summary>
    public string? DecisionNote { get; private set; }

    public Guid? RecurringExpenseId { get; private set; }

    /// <summary>The month (<c>yyMM</c>) of the recurring template this expense was generated for.</summary>
    public string? RecurringPeriod { get; private set; }

    public IReadOnlyList<ExpenseAttachment> Attachments => _attachments;

    public void Update(ExpenseDetails details)
    {
        Require(ExpenseStatus.Draft, "edited");
        Apply(details);
    }

    private void Apply(ExpenseDetails d)
    {
        if (d.Amount <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(d), "The amount must be positive.");
        }

        CategoryId = d.CategoryId;
        ExpenseDate = d.ExpenseDate;
        Amount = d.Amount;
        Method = d.Method;
        AccountId = d.AccountId;
        SupplierId = d.SupplierId;
        Reference = string.IsNullOrWhiteSpace(d.Reference) ? null : d.Reference.Trim();
        Notes = string.IsNullOrWhiteSpace(d.Notes) ? null : d.Notes.Trim();
    }

    public void Submit(DateTimeOffset now, Guid? by)
    {
        MoveTo(ExpenseStatus.Submitted);
        SubmittedAt = now;
        SubmittedBy = by;
        DecidedAt = null;
        DecidedBy = null;
        DecidedByName = null;
        DecisionNote = null;
    }

    public void Approve(DateTimeOffset now, Guid? by, string? byName, string? note, bool auto)
    {
        MoveTo(ExpenseStatus.Approved);
        DecidedAt = now;
        DecidedBy = by;
        DecidedByName = byName;
        AutoApproved = auto;
        DecisionNote = string.IsNullOrWhiteSpace(note) ? null : note.Trim();
    }

    public void Reject(DateTimeOffset now, Guid? by, string? byName, string reason)
    {
        MoveTo(ExpenseStatus.Rejected);
        DecidedAt = now;
        DecidedBy = by;
        DecidedByName = byName;
        DecisionNote = reason.Trim();
    }

    /// <summary>Submitted back to Draft (the author takes it back) or Rejected back to Draft (to fix it and send it again).</summary>
    public void ReturnToDraft()
    {
        MoveTo(ExpenseStatus.Draft);
        SubmittedAt = null;
        SubmittedBy = null;
    }

    public ExpenseAttachment AddAttachment(string path, string fileName, string contentType, long size)
    {
        var attachment = new ExpenseAttachment(Id, path, fileName, contentType, size);
        _attachments.Add(attachment);
        return attachment;
    }

    private void MoveTo(ExpenseStatus to)
    {
        if (!ExpenseStateMachine.CanMove(Status, to))
        {
            throw new InvalidOperationException($"An expense that is {Status} cannot become {to}.");
        }

        Status = to;
    }

    private void Require(ExpenseStatus expected, string verb)
    {
        if (Status != expected)
        {
            throw new InvalidOperationException($"Only a {expected} expense can be {verb}; this one is {Status}.");
        }
    }
}

/// <summary>A receipt or bill scanned or photographed for an expense (stored through the file storage).</summary>
public sealed class ExpenseAttachment : Entity
{
    private ExpenseAttachment()
    {
    }

    internal ExpenseAttachment(Guid expenseId, string path, string fileName, string contentType, long size)
    {
        ExpenseId = expenseId;
        Path = path;
        FileName = fileName;
        ContentType = contentType;
        Size = size;
    }

    public Guid ExpenseId { get; private set; }

    public string Path { get; private set; } = string.Empty;

    public string FileName { get; private set; } = string.Empty;

    public string ContentType { get; private set; } = string.Empty;

    public long Size { get; private set; }
}

/// <summary>
/// A monthly expense template (rent, salaries, internet). A background job generates one expense per month from the start date, once only
/// (a unique index on template and month keeps it idempotent).
/// </summary>
public sealed class RecurringExpense : Entity, IBranchScoped
{
    private RecurringExpense()
    {
    }

    public RecurringExpense(Guid branchId, string title, ExpenseDetails details, int dayOfMonth, DateOnly startDate, DateOnly? endDate)
    {
        BranchId = branchId;
        Update(title, details, dayOfMonth, startDate, endDate);
    }

    public Guid BranchId { get; private set; }

    public string Title { get; private set; } = string.Empty;

    public Guid CategoryId { get; private set; }

    public decimal Amount { get; private set; }

    public MoneyMethod Method { get; private set; }

    public Guid AccountId { get; private set; }

    public Guid? SupplierId { get; private set; }

    public string? Notes { get; private set; }

    /// <summary>1 to 28, so every month has the day.</summary>
    public int DayOfMonth { get; private set; }

    public DateOnly StartDate { get; private set; }

    public DateOnly? EndDate { get; private set; }

    public bool IsActive { get; private set; } = true;

    /// <summary>The last month (<c>yyMM</c>) an expense was generated for; null before the first run.</summary>
    public string? LastGeneratedPeriod { get; private set; }

    public void Update(string title, ExpenseDetails details, int dayOfMonth, DateOnly startDate, DateOnly? endDate)
    {
        if (details.Amount <= 0 || dayOfMonth is < 1 or > 28 || endDate is { } end && end < startDate)
        {
            throw new ArgumentException("A recurring expense needs a positive amount, a day from 1 to 28 and an end date after the start.");
        }

        Title = title.Trim();
        CategoryId = details.CategoryId;
        Amount = details.Amount;
        Method = details.Method;
        AccountId = details.AccountId;
        SupplierId = details.SupplierId;
        Notes = string.IsNullOrWhiteSpace(details.Notes) ? null : details.Notes.Trim();
        DayOfMonth = dayOfMonth;
        StartDate = startDate;
        EndDate = endDate;
    }

    public void SetActive(bool active) => IsActive = active;

    public void MarkGenerated(string period) => LastGeneratedPeriod = period;
}
