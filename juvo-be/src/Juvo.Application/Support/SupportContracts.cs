using Juvo.Domain.Support;
using Juvo.Shared.Pagination;

namespace Juvo.Application.Support;

// ---- Reading -------------------------------------------------------------------------------------------------

public sealed record TicketAttachmentDto(Guid Id, string FileName, string ContentType, long Size, string Url);

/// <summary>One message of the thread. <c>AuthorId</c> is only filled for staff views; the customer sees staff as "Juvo BD Support".</summary>
public sealed record TicketMessageDto(Guid Id, TicketMessageKind Kind, string Text, Guid? AuthorId, string? AuthorName, DateTimeOffset At, IReadOnlyList<TicketAttachmentDto> Attachments);

public sealed record TicketHistoryDto(TicketStatus? FromStatus, TicketStatus ToStatus, string? Reason, string? ActorName, DateTimeOffset At);

public sealed record TicketListItemDto(
    Guid Id, string Number, string Subject, TicketStatus Status, TicketPriority Priority, TicketChannel Channel, string Category, string CustomerMobile, string? CustomerName, Guid? CustomerId,
    Guid? OrderId, string? OrderNumber, Guid? BranchId, string? BranchCode, Guid? AssigneeId, string? AssigneeName, DateTimeOffset CreatedAt, DateTimeOffset LastActivityAt,
    DateTimeOffset ResponseDueAt, DateTimeOffset ResolveDueAt, DateTimeOffset? FirstResponseAt, bool Overdue, int MessageCount);

public sealed record TicketDetailDto(
    Guid Id, string Number, string Subject, TicketStatus Status, TicketPriority Priority, TicketChannel Channel, string Category, string CustomerMobile, string? CustomerName, string? CustomerEmail,
    Guid? CustomerId, Guid? OrderId, string? OrderNumber, Guid? BranchId, string? BranchCode, Guid? AssigneeId, string? AssigneeName, DateTimeOffset CreatedAt, DateTimeOffset LastActivityAt,
    DateTimeOffset ResponseDueAt, DateTimeOffset ResolveDueAt, DateTimeOffset? FirstResponseAt, DateTimeOffset? ResolvedAt, DateTimeOffset? ClosedAt, int ReopenCount, bool Overdue,
    bool CanReopen, IReadOnlyList<string> NextStatuses, IReadOnlyList<TicketMessageDto> Messages, IReadOnlyList<TicketHistoryDto> History);

public sealed record TicketListQuery(
    TicketStatus? Status, TicketPriority? Priority, Guid? AssigneeId, Guid? BranchId, string? OrderNumber, TicketChannel? Channel, string? Category, DateOnly? From, DateOnly? To, bool? Unassigned, bool? Overdue,
    bool? Active);

/// <summary>Counts for the tabs of the ticket screen, over the tickets the caller may see.</summary>
public sealed record TicketSummaryDto(IReadOnlyDictionary<string, int> ByStatus, int Active, int Unassigned, int Overdue, int Mine);

public sealed record MyTicketListItemDto(Guid Id, string Number, string Subject, TicketStatus Status, string Category, string? OrderNumber, DateTimeOffset CreatedAt, DateTimeOffset LastActivityAt);

public sealed record MyTicketDto(
    Guid Id, string Number, string Subject, TicketStatus Status, string Category, string? OrderNumber, DateTimeOffset CreatedAt, DateTimeOffset LastActivityAt, bool CanReply, bool CanClose, bool CanReopen,
    IReadOnlyList<TicketMessageDto> Messages);

// ---- Writing -------------------------------------------------------------------------------------------------

/// <summary>A signed-in customer opens a ticket. <c>OrderNumber</c> must be one of their own orders.</summary>
public sealed record CreateMyTicketRequest(string? Subject, string? Message, string? Category, string? OrderNumber, IReadOnlyList<string>? AttachmentPaths);

/// <summary>The public contact form. A guest links an order only with its number and the mobile number that placed it.</summary>
public sealed record ContactRequest(string? Name, string? Mobile, string? Email, string? Subject, string? Message, string? Category, string? OrderNumber);

public sealed record ContactReceiptDto(string Number);

/// <summary>Staff open a ticket for a customer who phoned, wrote on WhatsApp or Facebook, or came to the shop.</summary>
public sealed record StaffCreateTicketRequest(
    string? Mobile, string? CustomerName, string? Email, string? Subject, string? Message, TicketChannel? Channel, TicketPriority? Priority, string? Category, string? OrderNumber, Guid? AssigneeId,
    IReadOnlyList<string>? AttachmentPaths);

/// <summary>A staff reply. <c>Internal</c> true makes it a note only staff can read. <c>SetStatus</c> moves the ticket after the message (for example Waiting or Resolved).</summary>
public sealed record StaffReplyRequest(string? Text, bool Internal, TicketStatus? SetStatus, IReadOnlyList<string>? AttachmentPaths);

public sealed record CustomerReplyRequest(string? Text, IReadOnlyList<string>? AttachmentPaths);

public sealed record AssignTicketRequest(Guid? AssigneeId);

public sealed record ChangePriorityRequest(TicketPriority Priority);

public sealed record ChangeStatusRequest(TicketStatus Status, string? Reason);

public sealed record ReasonRequest(string? Reason);

/// <summary>Action is <c>assign</c> (needs AssigneeId, or null to unassign) or <c>close</c>.</summary>
public sealed record BulkTicketsRequest(IReadOnlyList<Guid>? Ids, string? Action, Guid? AssigneeId, string? Reason);

public sealed record BulkItemDto(Guid Id, bool Ok, string? Code, string? Message);

public sealed record BulkResultDto(int Total, int Succeeded, int Failed, IReadOnlyList<BulkItemDto> Items);

public sealed record TicketUploadDto(string Path, string FileName, string ContentType, long Size, string Url);

// ---- Canned replies ------------------------------------------------------------------------------------------

public sealed record CannedReplyDto(Guid Id, string Title, string Body, string Language, string? Category, bool IsActive, IReadOnlyList<string> Placeholders);

public sealed record SaveCannedReplyRequest(string? Title, string? Body, string? Language, string? Category, bool? IsActive);

/// <summary>Fill the placeholders of a canned reply (by id) or of any text for a ticket (optional). Answers the text, and which placeholders had no value.</summary>
public sealed record RenderCannedReplyRequest(Guid? CannedReplyId, string? Body, Guid? TicketId);

public sealed record RenderedReplyDto(string Text, IReadOnlyList<string> UnknownPlaceholders, IReadOnlyList<string> EmptyPlaceholders);

public sealed record CannedReplyQuery(string? Language, string? Category, bool IncludeInactive);

// ---- Report --------------------------------------------------------------------------------------------------

public sealed record CountDto(string Key, int Count);

public sealed record DayCountDto(DateOnly Date, int New, int Resolved);

public sealed record DurationStatsDto(int Count, double? AverageMinutes, double? MedianMinutes);

public sealed record AssigneeLoadDto(Guid? AssigneeId, string Name, int Total, int Open, int Resolved);

public sealed record OldestOpenDto(Guid Id, string Number, string Subject, TicketStatus Status, TicketPriority Priority, DateTimeOffset CreatedAt, double AgeHours);

public sealed record SupportReportDto(
    DateOnly From, DateOnly To, Guid? BranchId, int NewTickets, int ResolvedTickets, int OpenNow, int OverdueNow, IReadOnlyList<CountDto> OpenByStatus, IReadOnlyList<CountDto> OpenByPriority,
    IReadOnlyList<DayCountDto> Daily, DurationStatsDto FirstResponse, DurationStatsDto Resolution, IReadOnlyList<CountDto> ByCategory, IReadOnlyList<AssigneeLoadDto> ByAssignee,
    IReadOnlyList<OldestOpenDto> OldestOpen);
