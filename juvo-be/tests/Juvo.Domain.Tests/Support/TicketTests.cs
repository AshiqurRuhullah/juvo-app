using Juvo.Domain.Support;

namespace Juvo.Domain.Tests.Support;

public class TicketStateMachineTests
{
    private static readonly (TicketStatus From, TicketStatus To)[] Allowed =
    [
        (TicketStatus.Open, TicketStatus.InProgress), (TicketStatus.Open, TicketStatus.Waiting), (TicketStatus.Open, TicketStatus.Resolved), (TicketStatus.Open, TicketStatus.Closed),
        (TicketStatus.InProgress, TicketStatus.Waiting), (TicketStatus.InProgress, TicketStatus.Resolved), (TicketStatus.InProgress, TicketStatus.Closed), (TicketStatus.InProgress, TicketStatus.Open),
        (TicketStatus.Waiting, TicketStatus.InProgress), (TicketStatus.Waiting, TicketStatus.Resolved), (TicketStatus.Waiting, TicketStatus.Closed), (TicketStatus.Waiting, TicketStatus.Open),
        (TicketStatus.Resolved, TicketStatus.Closed), (TicketStatus.Resolved, TicketStatus.Open),
        (TicketStatus.Closed, TicketStatus.Open),
    ];

    [Fact]
    public void Exactly_the_transitions_of_the_specification_are_allowed_for_every_pair_of_statuses()
    {
        foreach (var from in Enum.GetValues<TicketStatus>())
        {
            foreach (var to in Enum.GetValues<TicketStatus>())
            {
                var expected = Allowed.Contains((from, to));
                Assert.True(expected == TicketStateMachine.CanMove(from, to), $"{from} -> {to} should be {(expected ? "allowed" : "refused")}");
            }
        }
    }

    [Fact]
    public void Moving_to_the_same_status_is_never_allowed()
    {
        Assert.All(Enum.GetValues<TicketStatus>(), s => Assert.False(TicketStateMachine.CanMove(s, s)));
    }

    [Fact]
    public void Only_a_move_from_resolved_or_closed_back_to_open_is_a_reopening()
    {
        foreach (var from in Enum.GetValues<TicketStatus>())
        {
            foreach (var to in Enum.GetValues<TicketStatus>())
            {
                var expected = to == TicketStatus.Open && from is TicketStatus.Resolved or TicketStatus.Closed;
                Assert.True(expected == TicketStateMachine.IsReopen(from, to), $"{from} -> {to}");
            }
        }
    }

    [Fact]
    public void Resolved_and_closed_tickets_are_not_active()
    {
        Assert.True(TicketStateMachine.IsActive(TicketStatus.Open));
        Assert.True(TicketStateMachine.IsActive(TicketStatus.InProgress));
        Assert.True(TicketStateMachine.IsActive(TicketStatus.Waiting));
        Assert.False(TicketStateMachine.IsActive(TicketStatus.Resolved));
        Assert.False(TicketStateMachine.IsActive(TicketStatus.Closed));
    }

    [Fact]
    public void NextFor_lists_the_same_moves_as_CanMove()
    {
        foreach (var from in Enum.GetValues<TicketStatus>())
        {
            Assert.Equal(Allowed.Where(a => a.From == from).Select(a => a.To).Order(), TicketStateMachine.NextFor(from).Order());
        }
    }
}

public class TicketAggregateTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 11, 9, 0, 0, TimeSpan.FromHours(6));

    private static Ticket MakeTicket(Guid? branch = null) =>
        Ticket.Open(new NewTicket("TK-2611-000001", TicketChannel.Web, "  Where is my parcel?  ", TicketPriority.Normal, "delivery", "+8801712345678", "Nadia", "nadia@example.com", null, null, null, null, null, branch,
            T0.AddHours(24), T0.AddHours(72)), "  It was due yesterday.  ", "Nadia", null, [], T0);

    [Fact]
    public void A_new_ticket_is_open_with_its_first_message_history_row_and_created_event()
    {
        var t = MakeTicket();

        Assert.Equal(TicketStatus.Open, t.Status);
        Assert.Equal("Where is my parcel?", t.Subject);
        var message = Assert.Single(t.Messages);
        Assert.Equal(TicketMessageKind.Customer, message.Kind);
        Assert.Equal("It was due yesterday.", message.Text);
        var row = Assert.Single(t.History);
        Assert.Null(row.FromStatus);
        Assert.Equal(TicketStatus.Open, row.ToStatus);
        var created = Assert.IsType<TicketCreated>(Assert.Single(t.DomainEvents));
        Assert.Equal("nadia@example.com", created.CustomerEmail);
        Assert.Null(t.FirstResponseAt);
    }

    [Fact]
    public void A_staff_message_is_the_first_response_and_raises_a_reply_event_but_a_note_does_not()
    {
        var t = MakeTicket();
        t.ClearDomainEvents();

        t.AddMessage(TicketMessageKind.Internal, "Check with the courier", Guid.NewGuid(), "Rina", [], T0.AddMinutes(5));
        Assert.Empty(t.DomainEvents);
        Assert.Null(t.FirstResponseAt);

        var reply = t.AddMessage(TicketMessageKind.Staff, "We are checking.", Guid.NewGuid(), "Rina", [], T0.AddMinutes(30));
        Assert.Equal(T0.AddMinutes(30), t.FirstResponseAt);
        var replied = Assert.IsType<TicketReplied>(Assert.Single(t.DomainEvents));
        Assert.Equal(reply.Id, replied.MessageId);

        t.AddMessage(TicketMessageKind.Staff, "Found it.", null, "Rina", [], T0.AddHours(2));
        Assert.Equal(T0.AddMinutes(30), t.FirstResponseAt); // the first response stays the first
    }

    [Fact]
    public void Every_move_writes_a_history_row_and_resolving_raises_the_resolved_event()
    {
        var t = MakeTicket();
        t.ClearDomainEvents();

        t.MoveTo(TicketStatus.InProgress, "Started", Guid.NewGuid(), "Rina", T0.AddHours(1));
        t.MoveTo(TicketStatus.Resolved, "Parcel found", Guid.NewGuid(), "Rina", T0.AddHours(3));

        Assert.Equal(3, t.History.Count);
        Assert.Equal(TicketStatus.Open, t.History[1].FromStatus);
        Assert.Equal(TicketStatus.InProgress, t.History[1].ToStatus);
        Assert.Equal(TicketStatus.InProgress, t.History[2].FromStatus);
        Assert.Equal(TicketStatus.Resolved, t.History[2].ToStatus);
        Assert.Equal(T0.AddHours(3), t.ResolvedAt);
        Assert.IsType<TicketResolved>(Assert.Single(t.DomainEvents));
    }

    [Fact]
    public void An_illegal_move_throws_and_changes_nothing()
    {
        var t = MakeTicket();
        t.MoveTo(TicketStatus.Closed, null, null, "System", T0.AddHours(1));

        Assert.Throws<InvalidOperationException>(() => t.MoveTo(TicketStatus.Resolved, null, null, null, T0.AddHours(2)));
        Assert.Equal(TicketStatus.Closed, t.Status);
        Assert.Equal(2, t.History.Count);
    }

    [Fact]
    public void Reopening_clears_the_resolved_and_closed_times_and_counts_the_reopening()
    {
        var t = MakeTicket();
        t.MoveTo(TicketStatus.Resolved, null, null, "Rina", T0.AddHours(1));
        t.MoveTo(TicketStatus.Closed, null, null, "System", T0.AddDays(8));
        Assert.NotNull(t.ClosedAt);

        t.MoveTo(TicketStatus.Open, "Customer replied", null, "Nadia", T0.AddDays(9));

        Assert.Equal(1, t.ReopenCount);
        Assert.Null(t.ResolvedAt);
        Assert.Null(t.ClosedAt);
    }

    [Fact]
    public void The_reopen_window_counts_from_the_resolution_or_the_closing()
    {
        var t = MakeTicket();
        t.MoveTo(TicketStatus.Resolved, null, null, "Rina", T0);
        Assert.True(t.WithinReopenWindow(T0.AddDays(7), 7));
        Assert.False(t.WithinReopenWindow(T0.AddDays(7).AddMinutes(1), 7));

        t.MoveTo(TicketStatus.Closed, null, null, "System", T0.AddDays(10));
        Assert.True(t.WithinReopenWindow(T0.AddDays(16), 7));
        Assert.False(t.WithinReopenWindow(T0.AddDays(18), 7));
    }

    [Fact]
    public void Assigning_and_unassigning_changes_the_assignee_name_with_it()
    {
        var t = MakeTicket();
        var user = Guid.NewGuid();

        t.Assign(user, "Rina", T0.AddMinutes(1));
        Assert.Equal(user, t.AssigneeId);
        Assert.Equal("Rina", t.AssigneeName);

        t.Assign(null, "ignored", T0.AddMinutes(2));
        Assert.Null(t.AssigneeId);
        Assert.Null(t.AssigneeName);
    }

    [Fact]
    public void A_ticket_is_overdue_while_worked_on_but_not_while_waiting_for_the_customer()
    {
        var t = MakeTicket();
        Assert.False(t.IsOverdue(T0.AddHours(23)));
        Assert.True(t.IsOverdue(T0.AddHours(25))); // no first response and the response is past due

        t.AddMessage(TicketMessageKind.Staff, "Hello", null, "Rina", [], T0.AddHours(2));
        Assert.False(t.IsOverdue(T0.AddHours(25))); // answered in time; resolution is due at 72 h
        Assert.True(t.IsOverdue(T0.AddHours(73)));

        t.MoveTo(TicketStatus.Waiting, null, null, "Rina", T0.AddHours(3));
        Assert.False(t.IsOverdue(T0.AddHours(200)));
    }

    [Fact]
    public void Reprioritising_replaces_the_due_times_and_linking_an_order_sets_the_branch()
    {
        var t = MakeTicket();
        var branch = Guid.NewGuid();

        t.Reprioritize(TicketPriority.Urgent, T0.AddHours(2), T0.AddHours(8), T0.AddMinutes(1));
        t.LinkOrder(Guid.NewGuid(), "JV-HO-2611-000001", branch, T0.AddMinutes(2));

        Assert.Equal(TicketPriority.Urgent, t.Priority);
        Assert.Equal(T0.AddHours(8), t.ResolveDueAt);
        Assert.Equal(branch, t.BranchId);
        Assert.Equal("JV-HO-2611-000001", t.OrderNumber);
    }

    [Theory]
    [InlineData("order", true)]
    [InlineData("delivery", true)]
    [InlineData("other", true)]
    [InlineData("Order", false)]
    [InlineData("complaint", false)]
    [InlineData(null, false)]
    public void Only_the_known_categories_are_valid(string? code, bool valid) => Assert.Equal(valid, TicketCategories.IsValid(code));

    [Fact]
    public void A_canned_reply_keeps_its_language_and_can_be_switched_off()
    {
        var reply = new CannedReply("  Sorry for the wait ", "  Hello {customerName}  ", "bn", " ");

        Assert.Equal("Sorry for the wait", reply.Title);
        Assert.Equal("Hello {customerName}", reply.Body);
        Assert.Equal("bn", reply.Language);
        Assert.Null(reply.Category);
        Assert.True(reply.IsActive);

        reply.SetActive(false);
        Assert.False(reply.IsActive);
    }
}
