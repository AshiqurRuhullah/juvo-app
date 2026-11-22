using Juvo.Domain.Content;

namespace Juvo.Application.Content;

/// <summary>Public contact form. <c>Website</c> is the honeypot: a human never sees or fills it.</summary>
public sealed record ContactFormRequest(string? Name, string? Mobile, string? Email, string? Subject, string? Message, string? SourcePage, string? Website);

public sealed record ContactReceiptDto(string Reference);

/// <summary><c>PiiMasked</c> is true when the caller lacks <c>customers.customer.view-pii</c>: mobile and email are then partly hidden.</summary>
public sealed record ContactMessageListItemDto(
    Guid Id, string Reference, string Name, string? Mobile, string? Email, string? Subject, string MessagePreview, ContactMessageStatus Status, DateTimeOffset ReceivedAt, bool PiiMasked);

public sealed record ContactMessageDto(
    Guid Id, string Reference, string Name, string? Mobile, string? Email, string? Subject, string Message, ContactMessageStatus Status, string? SourcePage, DateTimeOffset ReceivedAt,
    Guid? HandledBy, DateTimeOffset? HandledAt, string? InternalNote, bool PiiMasked);

public sealed record SetContactStatusRequest(ContactMessageStatus Status);

public sealed record AddContactNoteRequest(string? Note);

public sealed record ContactMessageQuery(ContactMessageStatus? Status);
