using Juvo.Domain.Accounting;

namespace Juvo.Domain.Tests.Accounting;

public class ExpenseStateMachineTests
{
    private static readonly (ExpenseStatus From, ExpenseStatus To)[] Allowed =
    [
        (ExpenseStatus.Draft, ExpenseStatus.Submitted),
        (ExpenseStatus.Submitted, ExpenseStatus.Approved), (ExpenseStatus.Submitted, ExpenseStatus.Rejected), (ExpenseStatus.Submitted, ExpenseStatus.Draft),
        (ExpenseStatus.Rejected, ExpenseStatus.Draft),
    ];

    [Fact]
    public void Exactly_the_documented_transitions_are_allowed()
    {
        foreach (var from in Enum.GetValues<ExpenseStatus>())
        {
            foreach (var to in Enum.GetValues<ExpenseStatus>())
            {
                var expected = Allowed.Contains((from, to));
                Assert.True(expected == ExpenseStateMachine.CanMove(from, to), $"{from} -> {to} should be {(expected ? "allowed" : "refused")}");
            }
        }
    }
}

public class ExpenseAggregateTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 11, 9, 0, 0, TimeSpan.FromHours(6));

    private static Expense NewExpense(decimal amount = 100m) =>
        new("EXP-HO-2611-000001", Guid.NewGuid(), new ExpenseDetails(Guid.NewGuid(), new DateOnly(2026, 11, 10), amount, MoneyMethod.Cash, Guid.NewGuid(), null, " ref ", null), null, null);

    [Fact]
    public void A_new_expense_is_a_draft_with_cleaned_text_and_a_positive_amount_is_required()
    {
        var e = NewExpense();
        Assert.Equal(ExpenseStatus.Draft, e.Status);
        Assert.Equal("ref", e.Reference);
        Assert.Throws<ArgumentOutOfRangeException>(() => NewExpense(0m));
        Assert.Throws<ArgumentOutOfRangeException>(() => NewExpense(-5m));
    }

    [Fact]
    public void The_full_happy_path_stamps_who_and_when()
    {
        var e = NewExpense();
        var author = Guid.NewGuid();
        var boss = Guid.NewGuid();
        e.Submit(Now, author);
        Assert.Equal(ExpenseStatus.Submitted, e.Status);
        Assert.Equal(author, e.SubmittedBy);
        e.Approve(Now.AddHours(1), boss, "Boss", " fine ", auto: false);
        Assert.Equal(ExpenseStatus.Approved, e.Status);
        Assert.Equal((boss, "Boss", "fine", false), (e.DecidedBy, e.DecidedByName, e.DecisionNote, e.AutoApproved));
    }

    [Fact]
    public void Only_a_draft_can_be_edited_and_an_approved_expense_is_final()
    {
        var e = NewExpense();
        e.Submit(Now, null);
        Assert.Throws<InvalidOperationException>(() => e.Update(new ExpenseDetails(Guid.NewGuid(), new DateOnly(2026, 11, 10), 5m, MoneyMethod.Bank, Guid.NewGuid(), null, null, null)));
        e.Approve(Now, null, "System", null, auto: true);
        Assert.Throws<InvalidOperationException>(e.ReturnToDraft);
        Assert.Throws<InvalidOperationException>(() => e.Reject(Now, null, "x", "late"));
        Assert.Throws<InvalidOperationException>(() => e.Submit(Now, null));
    }

    [Fact]
    public void A_rejected_or_withdrawn_expense_returns_to_draft_and_forgets_the_decision()
    {
        var e = NewExpense();
        e.Submit(Now, Guid.NewGuid());
        e.Reject(Now, Guid.NewGuid(), "Boss", "No receipt");
        Assert.Equal("No receipt", e.DecisionNote);
        e.ReturnToDraft();
        Assert.Equal(ExpenseStatus.Draft, e.Status);
        e.Submit(Now, Guid.NewGuid());
        Assert.Null(e.DecisionNote);
        e.ReturnToDraft(); // withdrawn
        Assert.Equal(ExpenseStatus.Draft, e.Status);
        Assert.Null(e.SubmittedAt);
    }
}

public class LedgerAndRecurringTests
{
    [Fact]
    public void A_ledger_entry_needs_a_non_zero_amount()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new LedgerEntry(Guid.NewGuid(), Guid.NewGuid(), new DateOnly(2026, 11, 1), LedgerEntryKind.Adjustment, 0m, "x", null, null, null, null));
        var entry = new LedgerEntry(Guid.NewGuid(), Guid.NewGuid(), new DateOnly(2026, 11, 1), LedgerEntryKind.Expense, -5m, " Rent ", "Expense", Guid.NewGuid(), null, "Ann");
        Assert.Equal("Rent", entry.Description);
    }

    [Fact]
    public void A_recurring_template_needs_a_day_from_one_to_twenty_eight_and_a_sensible_period()
    {
        var details = new ExpenseDetails(Guid.NewGuid(), new DateOnly(2026, 1, 1), 100m, MoneyMethod.Bank, Guid.NewGuid(), null, null, null);
        Assert.Throws<ArgumentException>(() => new RecurringExpense(Guid.NewGuid(), "Rent", details, 29, new DateOnly(2026, 1, 1), null));
        Assert.Throws<ArgumentException>(() => new RecurringExpense(Guid.NewGuid(), "Rent", details, 0, new DateOnly(2026, 1, 1), null));
        Assert.Throws<ArgumentException>(() => new RecurringExpense(Guid.NewGuid(), "Rent", details, 5, new DateOnly(2026, 2, 1), new DateOnly(2026, 1, 1)));
        Assert.Throws<ArgumentException>(() => new RecurringExpense(Guid.NewGuid(), "Rent", details with { Amount = 0m }, 5, new DateOnly(2026, 1, 1), null));
        var ok = new RecurringExpense(Guid.NewGuid(), "Rent", details, 28, new DateOnly(2026, 1, 1), null);
        Assert.True(ok.IsActive);
    }
}
