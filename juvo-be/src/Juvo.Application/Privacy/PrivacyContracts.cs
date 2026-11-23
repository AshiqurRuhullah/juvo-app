namespace Juvo.Application.Privacy;

/// <summary>
/// One consent change. <c>Channel</c> is <c>Sms</c>, <c>Email</c> or <c>Policy</c>; the SMS consent also covers WhatsApp (there is no separate WhatsApp consent, see
/// <c>AppliesTo</c>). <c>OldValue</c> is null for the first record of a type. <c>SourceKind</c> is <c>Customer</c>, <c>Staff</c>, <c>UnsubscribeLink</c>, <c>Import</c> or <c>Other</c>;
/// <c>Source</c> is the stored text. <c>IpAddress</c> is partly hidden without <c>customers.customer.view-pii</c>.
/// </summary>
public sealed record ConsentHistoryEntryDto(
    Guid Id, string Type, string Channel, IReadOnlyList<string> AppliesTo, bool? OldValue, bool NewValue, string Source, string SourceKind, string? IpAddress, DateTimeOffset At);

public sealed record ConsentHistoryDto(Guid CustomerId, bool PiiMasked, IReadOnlyList<ConsentHistoryEntryDto> Items);

public sealed record AnonymiseCustomerRequest(string? Reason, string? Confirm);

/// <summary>How many rows of each kind were scrubbed or removed. All counts are zero on a repeated call.</summary>
public sealed record AnonymiseCountsDto(
    int Orders, int Returns, int Tickets, int TicketMessages, int ContactMessages, int NotificationLogs, int Shipments, int Payments, int AuditRows, int OutboxMessages,
    int AddressesRemoved, int WishlistRemoved, int SessionsRemoved, int CartsRemoved, int OtpCodesRemoved, int SignInRows, int ConsentRows, int LoyaltyRows, int OtherRows);

public sealed record AnonymiseResultDto(
    Guid CustomerId, bool AlreadyAnonymised, DateTimeOffset AnonymisedAt, AnonymiseCountsDto Scrubbed, IReadOnlyList<string> Kept, int ForfeitedPoints, decimal ForfeitedCredit);

/// <summary>The downloadable file. The content type is <c>application/json</c>.</summary>
public sealed record PrivacyExportFile(string FileName, string ContentType, byte[] Content);
