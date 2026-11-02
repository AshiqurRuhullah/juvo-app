namespace Juvo.Domain.Common;

/// <summary>
/// Data that belongs to one branch (orders, stock, expenses, cash book, tickets, POS...). A global query filter hides other
/// branches' rows from branch-scoped staff, and the save interceptor rejects writes into branches the user does not hold.
/// </summary>
public interface IBranchScoped
{
    Guid BranchId { get; }
}
