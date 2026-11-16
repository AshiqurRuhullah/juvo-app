using Juvo.Domain.Accounting;
using Juvo.Shared.Pagination;

namespace Juvo.Application.Accounting;

// ---- Accounts and cash book ----

public sealed record AccountDto(
    Guid Id, Guid BranchId, string BranchCode, string Name, AccountType Type, string? AccountNumber, string? Note, decimal OpeningBalance, DateOnly OpeningDate, bool IsActive, decimal Balance);

public sealed record SaveAccountRequest(Guid? BranchId, string Name, AccountType Type, string? AccountNumber, string? Note, decimal OpeningBalance, DateOnly? OpeningDate);

public sealed record UpdateAccountRequest(string Name, AccountType Type, string? AccountNumber, string? Note, bool IsActive);

public sealed record TransferRequest(Guid FromAccountId, Guid ToAccountId, decimal Amount, DateOnly? Date, string? Note);

public sealed record AdjustmentRequest(Guid AccountId, decimal Amount, DateOnly? Date, string Reason);

public sealed record LedgerEntryDto(Guid Id, Guid AccountId, string AccountName, DateOnly EntryDate, LedgerEntryKind Kind, decimal Amount, string Description, string? SourceType, Guid? SourceId, Guid? TransferId, string? ActorName);

public sealed record TransferResultDto(Guid TransferId, LedgerEntryDto Out, LedgerEntryDto In);

public sealed record AccountBalanceDto(
    Guid Id, string Name, AccountType Type, Guid BranchId, string BranchCode, bool IsActive, decimal Opening, decimal MoneyIn, decimal MoneyOut, decimal Balance);

/// <summary>Balances of every account as of a date. <c>DerivedReceipts</c> is the net of customer payments, settled COD cash and completed refunds in the same period: money that is read from the payment data and not assigned to an account.</summary>
public sealed record AccountBalancesDto(DateOnly AsOf, IReadOnlyList<AccountBalanceDto> Accounts, decimal Total, decimal DerivedReceipts);

public sealed record DaybookEntryDto(
    DateOnly Date, DateTimeOffset At, string Kind, decimal Amount, string Description, Guid BranchId, string BranchCode, Guid? AccountId, string? AccountName, string? SourceType, Guid? SourceId, bool Derived);

public sealed record DaybookDto(PagedResult<DaybookEntryDto> Entries, decimal TotalIn, decimal TotalOut, decimal Net, DateOnly From, DateOnly To);

public sealed record DaybookQuery(DateOnly? From, DateOnly? To, Guid? BranchId, Guid? AccountId);

// ---- Expenses ----

public sealed record ExpenseCategoryDto(Guid Id, string NameEn, string? NameBn, Guid? ParentId, bool IsActive, int ExpenseCount);

public sealed record SaveExpenseCategoryRequest(string NameEn, string? NameBn, Guid? ParentId, bool? IsActive);

public sealed record ExpenseAttachmentDto(Guid Id, string FileName, string ContentType, long Size, string Url);

public sealed record ExpenseListItemDto(
    Guid Id, string Number, Guid BranchId, string BranchCode, Guid CategoryId, string CategoryName, DateOnly ExpenseDate, decimal Amount, MoneyMethod Method, Guid AccountId, string AccountName,
    Guid? SupplierId, string? Reference, ExpenseStatus Status, bool AutoApproved, bool IsRecurring, int AttachmentCount, DateTimeOffset CreatedAt);

public sealed record ExpenseListDto(PagedResult<ExpenseListItemDto> Expenses, decimal TotalAmount);

public sealed record ExpenseDetailDto(
    Guid Id, string Number, Guid BranchId, string BranchCode, Guid CategoryId, string CategoryName, DateOnly ExpenseDate, decimal Amount, MoneyMethod Method, Guid AccountId, string AccountName,
    Guid? SupplierId, string? SupplierName, string? Reference, string? Notes, ExpenseStatus Status, bool AutoApproved, Guid? RecurringExpenseId, DateTimeOffset CreatedAt, DateTimeOffset? SubmittedAt,
    DateTimeOffset? DecidedAt, string? DecidedByName, string? DecisionNote, IReadOnlyList<ExpenseAttachmentDto> Attachments, IReadOnlyList<string> NextStatuses);

public sealed record SaveExpenseRequest(
    Guid? BranchId, Guid CategoryId, DateOnly ExpenseDate, decimal Amount, MoneyMethod Method, Guid AccountId, Guid? SupplierId, string? Reference, string? Notes, IReadOnlyList<string>? AttachmentPaths);

public sealed record ExpenseListQuery(ExpenseStatus? Status, Guid? CategoryId, Guid? BranchId, Guid? AccountId, Guid? SupplierId, DateOnly? From, DateOnly? To);

public sealed record DecideExpenseRequest(string? Note);

public sealed record RejectExpenseRequest(string? Reason);

public sealed record AttachmentUploadDto(string Path, string FileName, string ContentType, long Size, string Url);

// ---- Recurring expenses ----

public sealed record RecurringExpenseDto(
    Guid Id, Guid BranchId, string BranchCode, string Title, Guid CategoryId, string CategoryName, decimal Amount, MoneyMethod Method, Guid AccountId, string AccountName, Guid? SupplierId, string? Notes,
    int DayOfMonth, DateOnly StartDate, DateOnly? EndDate, bool IsActive, string? LastGeneratedPeriod);

public sealed record SaveRecurringExpenseRequest(
    Guid? BranchId, string Title, Guid CategoryId, decimal Amount, MoneyMethod Method, Guid AccountId, Guid? SupplierId, string? Notes, int DayOfMonth, DateOnly StartDate, DateOnly? EndDate, bool? IsActive);

public sealed record GenerateRecurringResult(int Templates, int Created, int AutoApproved);
