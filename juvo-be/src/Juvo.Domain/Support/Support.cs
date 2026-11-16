using Juvo.Domain.Common;

namespace Juvo.Domain.Support;

/// <summary>Where the customer reached us. Stored as an integer; append new values at the end.</summary>
public enum TicketChannel
{
    Web = 1,
    Phone = 2,
    WhatsApp = 3,
    Facebook = 4,
    Email = 5,
    OrderPage = 6,
}

public enum TicketPriority
{
    Low = 1,
    Normal = 2,
    High = 3,
    Urgent = 4,
}

public enum TicketStatus
{
    Open = 1,
    InProgress = 2,
    Waiting = 3,
    Resolved = 4,
    Closed = 5,
}

public enum TicketMessageKind
{
    /// <summary>Written by the customer (or typed in by staff for a customer who phoned).</summary>
    Customer = 1,

    /// <summary>A reply staff sent to the customer.</summary>
    Staff = 2,

    /// <summary>A note for staff only. Never shown to the customer.</summary>
    Internal = 3,

    /// <summary>Written by the system (auto-close, reopen).</summary>
    System = 4,
}

public static class TicketCategories
{
    public static readonly IReadOnlyList<string> All = ["order", "delivery", "payment", "product", "return", "account", "other"];

    public static bool IsValid(string? code) => code is not null && All.Contains(code, StringComparer.Ordinal);
}

/// <summary>
/// The moves of a ticket: Open to InProgress, Waiting, Resolved or Closed; InProgress to Waiting, Resolved, Closed or back to Open; Waiting to InProgress, Resolved, Closed or back to
/// Open; Resolved to Closed or Open (reopened); Closed to Open (reopened). Reopening is limited by the reopen window (a setting), checked by the service.
/// </summary>
public static class TicketStateMachine
{
    private static readonly Dictionary<TicketStatus, TicketStatus[]> Moves = new()
    {
        [TicketStatus.Open] = [TicketStatus.InProgress, TicketStatus.Waiting, TicketStatus.Resolved, TicketStatus.Closed],
        [TicketStatus.InProgress] = [TicketStatus.Waiting, TicketStatus.Resolved, TicketStatus.Closed, TicketStatus.Open],
        [TicketStatus.Waiting] = [TicketStatus.InProgress, TicketStatus.Resolved, TicketStatus.Closed, TicketStatus.Open],
        [TicketStatus.Resolved] = [TicketStatus.Closed, TicketStatus.Open],
        [TicketStatus.Closed] = [TicketStatus.Open],
    };

    public static IReadOnlyList<TicketStatus> NextFor(TicketStatus from) => Moves[from];

    public static bool CanMove(TicketStatus from, TicketStatus to) => Moves[from].Contains(to);

    /// <summary>Moving back to Open from Resolved or Closed is a reopening and is limited to a number of days.</summary>
    public static bool IsReopen(TicketStatus from, TicketStatus to) => to == TicketStatus.Open && from is TicketStatus.Resolved or TicketStatus.Closed;

    public static bool IsActive(TicketStatus status) => status is not (TicketStatus.Resolved or TicketStatus.Closed);
}

public sealed record TicketCreated(DateTimeOffset At, Guid TicketId, string Number, string Subject, string CustomerMobile, Guid? CustomerId, string? CustomerName, string? CustomerEmail) : DomainEvent(At);

/// <summary>Staff sent a reply the customer can read.</summary>
public sealed record TicketReplied(DateTimeOffset At, Guid TicketId, string Number, string Subject, string CustomerMobile, Guid? CustomerId, string? CustomerName, string? CustomerEmail, Guid MessageId) : DomainEvent(At);

public sealed record TicketResolved(DateTimeOffset At, Guid TicketId, string Number, string Subject, string CustomerMobile, Guid? CustomerId, string? CustomerName, string? CustomerEmail) : DomainEvent(At);

public sealed record NewTicket(
    string Number, TicketChannel Channel, string Subject, TicketPriority Priority, string Category, string CustomerMobile, string? CustomerName, string? CustomerEmail, Guid? CustomerId, Guid? OrderId, string? OrderNumber,
    Guid? ReturnId, string? ReturnNumber, Guid? BranchId, DateTimeOffset ResponseDueAt, DateTimeOffset ResolveDueAt);

/// <summary>
/// A support ticket (module Y): a conversation with a customer about an order, a return or anything else. The customer is known by mobile number (BR-006); a guest
/// can write. The thread is append-only. A ticket tied to a branch's order belongs to that branch; otherwise it is global.
/// </summary>
public sealed class Ticket : AggregateRoot
{
    private readonly List<TicketMessage> _messages = [];
    private readonly List<TicketStatusHistory> _history = [];

    private Ticket()
    {
    }

    public string Number { get; private set; } = string.Empty;

    public TicketChannel Channel { get; private set; }

    public string Subject { get; private set; } = string.Empty;

    public TicketPriority Priority { get; private set; }

    public string Category { get; private set; } = "other";

    public TicketStatus Status { get; private set; } = TicketStatus.Open;

    public Guid? CustomerId { get; private set; }

    [AuditIgnore]
    public string CustomerMobile { get; private set; } = string.Empty;

    [AuditIgnore]
    public string? CustomerName { get; private set; }

    [AuditIgnore]
    public string? CustomerEmail { get; private set; }

    public Guid? OrderId { get; private set; }

    public string? OrderNumber { get; private set; }

    public Guid? ReturnId { get; private set; }

    public string? ReturnNumber { get; private set; }

    /// <summary>The branch of the linked order; null for a global ticket (visible to every staff member who may view tickets).</summary>
    public Guid? BranchId { get; private set; }

    public Guid? AssigneeId { get; private set; }

    public string? AssigneeName { get; private set; }

    /// <summary>When the first reply is due (by priority).</summary>
    public DateTimeOffset ResponseDueAt { get; private set; }

    /// <summary>When the ticket should be resolved (by priority).</summary>
    public DateTimeOffset ResolveDueAt { get; private set; }

    public DateTimeOffset? FirstResponseAt { get; private set; }

    public DateTimeOffset? ResolvedAt { get; private set; }

    public DateTimeOffset? ClosedAt { get; private set; }

    public DateTimeOffset LastActivityAt { get; private set; }

    public int ReopenCount { get; private set; }

    public IReadOnlyList<TicketMessage> Messages => _messages;

    public IReadOnlyList<TicketStatusHistory> History => _history;

    /// <summary>Creates the ticket with the customer's first message.</summary>
    public static Ticket Open(NewTicket data, string firstMessage, string? authorName, Guid? authorId, IReadOnlyList<NewTicketAttachment> attachments, DateTimeOffset now)
    {
        var ticket = new Ticket
        {
            Number = data.Number,
            Channel = data.Channel,
            Subject = data.Subject.Trim(),
            Priority = data.Priority,
            Category = data.Category,
            CustomerMobile = data.CustomerMobile,
            CustomerName = data.CustomerName,
            CustomerEmail = data.CustomerEmail,
            CustomerId = data.CustomerId,
            OrderId = data.OrderId,
            OrderNumber = data.OrderNumber,
            ReturnId = data.ReturnId,
            ReturnNumber = data.ReturnNumber,
            BranchId = data.BranchId,
            ResponseDueAt = data.ResponseDueAt,
            ResolveDueAt = data.ResolveDueAt,
            LastActivityAt = now,
        };
        ticket._messages.Add(new TicketMessage(ticket.Id, TicketMessageKind.Customer, firstMessage, authorId, authorName ?? data.CustomerName ?? "Customer", now, attachments));
        ticket._history.Add(new TicketStatusHistory(ticket.Id, null, TicketStatus.Open, "Ticket created", authorId, authorName, now));
        ticket.Raise(new TicketCreated(now, ticket.Id, ticket.Number, ticket.Subject, ticket.CustomerMobile, ticket.CustomerId, ticket.CustomerName, ticket.CustomerEmail));
        return ticket;
    }

    /// <summary>Adds a message to the thread. A staff reply is the first response of the ticket and tells the customer; an internal note never does.</summary>
    public TicketMessage AddMessage(TicketMessageKind kind, string text, Guid? authorId, string? authorName, IReadOnlyList<NewTicketAttachment> attachments, DateTimeOffset now)
    {
        var message = new TicketMessage(Id, kind, text, authorId, authorName, now, attachments);
        _messages.Add(message);
        LastActivityAt = now;
        if (kind == TicketMessageKind.Staff)
        {
            FirstResponseAt ??= now;
            Raise(new TicketReplied(now, Id, Number, Subject, CustomerMobile, CustomerId, CustomerName, CustomerEmail, message.Id));
        }

        return message;
    }

    /// <summary>Moves the ticket through <see cref="TicketStateMachine"/> and writes the history row. The reopen window is the caller's concern.</summary>
    public void MoveTo(TicketStatus to, string? reason, Guid? by, string? byName, DateTimeOffset now)
    {
        if (!TicketStateMachine.CanMove(Status, to))
        {
            throw new InvalidOperationException($"A ticket that is {Status} cannot become {to}.");
        }

        if (TicketStateMachine.IsReopen(Status, to))
        {
            ReopenCount++;
            ResolvedAt = null;
            ClosedAt = null;
        }

        _history.Add(new TicketStatusHistory(Id, Status, to, reason, by, byName, now));
        Status = to;
        LastActivityAt = now;
        if (to == TicketStatus.Resolved)
        {
            ResolvedAt = now;
            Raise(new TicketResolved(now, Id, Number, Subject, CustomerMobile, CustomerId, CustomerName, CustomerEmail));
        }
        else if (to == TicketStatus.Closed)
        {
            ClosedAt = now;
            ResolvedAt ??= now;
        }
    }

    /// <summary>Whether a Resolved or Closed ticket may still be reopened.</summary>
    public bool WithinReopenWindow(DateTimeOffset now, int days) =>
        Status switch
        {
            TicketStatus.Resolved => ResolvedAt is { } r && now - r <= TimeSpan.FromDays(days),
            TicketStatus.Closed => ClosedAt is { } c && now - c <= TimeSpan.FromDays(days),
            _ => true,
        };

    public void Assign(Guid? assigneeId, string? assigneeName, DateTimeOffset now)
    {
        AssigneeId = assigneeId;
        AssigneeName = assigneeId is null ? null : assigneeName;
        LastActivityAt = now;
    }

    public void Reprioritize(TicketPriority priority, DateTimeOffset responseDueAt, DateTimeOffset resolveDueAt, DateTimeOffset now)
    {
        Priority = priority;
        ResponseDueAt = responseDueAt;
        ResolveDueAt = resolveDueAt;
        LastActivityAt = now;
    }

    public void LinkOrder(Guid orderId, string orderNumber, Guid branchId, DateTimeOffset now)
    {
        OrderId = orderId;
        OrderNumber = orderNumber;
        BranchId = branchId;
        LastActivityAt = now;
    }

    public void SetCategory(string category) => Category = category;

    /// <summary>Overdue: still being worked on and the first reply, or the resolution, is past due. A ticket waiting for the customer is not overdue.</summary>
    public bool IsOverdue(DateTimeOffset now) =>
        Status is TicketStatus.Open or TicketStatus.InProgress && ((FirstResponseAt is null && now > ResponseDueAt) || now > ResolveDueAt);
}

public sealed record NewTicketAttachment(string Path, string FileName, string ContentType, long Size);

/// <summary>One message of the thread. Append-only.</summary>
public sealed class TicketMessage : Entity, IAppendOnly, INotAudited
{
    private readonly List<TicketAttachment> _attachments = [];

    private TicketMessage()
    {
    }

    internal TicketMessage(Guid ticketId, TicketMessageKind kind, string text, Guid? authorId, string? authorName, DateTimeOffset at, IReadOnlyList<NewTicketAttachment> attachments)
    {
        TicketId = ticketId;
        Kind = kind;
        Text = text.Trim();
        AuthorId = authorId;
        AuthorName = authorName;
        At = at;
        foreach (var a in attachments)
        {
            _attachments.Add(new TicketAttachment(Id, a.Path, a.FileName, a.ContentType, a.Size));
        }
    }

    public Guid TicketId { get; private set; }

    public TicketMessageKind Kind { get; private set; }

    public string Text { get; private set; } = string.Empty;

    public Guid? AuthorId { get; private set; }

    public string? AuthorName { get; private set; }

    public DateTimeOffset At { get; private set; }

    public IReadOnlyList<TicketAttachment> Attachments => _attachments;
}

public sealed class TicketAttachment : Entity, INotAudited
{
    private TicketAttachment()
    {
    }

    internal TicketAttachment(Guid messageId, string path, string fileName, string contentType, long size)
    {
        MessageId = messageId;
        Path = path;
        FileName = fileName;
        ContentType = contentType;
        Size = size;
    }

    public Guid MessageId { get; private set; }

    public string Path { get; private set; } = string.Empty;

    public string FileName { get; private set; } = string.Empty;

    public string ContentType { get; private set; } = string.Empty;

    public long Size { get; private set; }
}

/// <summary>One status change of a ticket. Append-only.</summary>
public sealed class TicketStatusHistory : Entity, IAppendOnly, INotAudited
{
    private TicketStatusHistory()
    {
    }

    internal TicketStatusHistory(Guid ticketId, TicketStatus? from, TicketStatus to, string? reason, Guid? actorId, string? actorName, DateTimeOffset at)
    {
        TicketId = ticketId;
        FromStatus = from;
        ToStatus = to;
        Reason = reason;
        ActorId = actorId;
        ActorName = actorName;
        At = at;
    }

    public Guid TicketId { get; private set; }

    public TicketStatus? FromStatus { get; private set; }

    public TicketStatus ToStatus { get; private set; }

    public string? Reason { get; private set; }

    public Guid? ActorId { get; private set; }

    public string? ActorName { get; private set; }

    public DateTimeOffset At { get; private set; }
}

/// <summary>A ready-made reply staff can insert (module Y). Kept per language.</summary>
public sealed class CannedReply : Entity
{
    private CannedReply()
    {
    }

    public CannedReply(string title, string body, string language, string? category)
    {
        Update(title, body, language, category);
    }

    public string Title { get; private set; } = string.Empty;

    public string Body { get; private set; } = string.Empty;

    /// <summary><c>en</c> or <c>bn</c>.</summary>
    public string Language { get; private set; } = "en";

    public string? Category { get; private set; }

    public bool IsActive { get; private set; } = true;

    public void Update(string title, string body, string language, string? category)
    {
        Title = title.Trim();
        Body = body.Trim();
        Language = language;
        Category = string.IsNullOrWhiteSpace(category) ? null : category.Trim();
    }

    public void SetActive(bool active) => IsActive = active;
}

/// <summary>Counter behind the gap-free numbers that belong to no branch (<c>TK-2611-000007</c>).</summary>
public sealed class GlobalSequence : Entity, INotAudited
{
    private GlobalSequence()
    {
    }

    public GlobalSequence(string kind, string period, int lastNumber)
    {
        Kind = kind;
        Period = period;
        LastNumber = lastNumber;
    }

    public string Kind { get; private set; } = string.Empty;

    /// <summary>Year and month as <c>yyMM</c>.</summary>
    public string Period { get; private set; } = string.Empty;

    public int LastNumber { get; private set; }
}
