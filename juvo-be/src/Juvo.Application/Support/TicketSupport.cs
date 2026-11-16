using System.Text.RegularExpressions;
using Juvo.Application.Abstractions;
using Juvo.Application.Operations;
using Juvo.Application.Orders;
using Juvo.Domain.Support;
using Juvo.Shared.Contacts;
using Juvo.Shared.Results;
using Microsoft.EntityFrameworkCore;

namespace Juvo.Application.Support;

/// <summary>Placeholders of a canned reply, written <c>{customerName}</c>, <c>{orderNumber}</c>, <c>{ticketNumber}</c>. Pure text logic, no database.</summary>
public static partial class CannedReplyTemplate
{
    public static readonly IReadOnlyList<string> Known = ["customerName", "orderNumber", "ticketNumber"];

    [GeneratedRegex(@"\{([A-Za-z][A-Za-z0-9]*)\}")]
    private static partial Regex Placeholder();

    /// <summary>Every placeholder a text uses, once each, in order of first use.</summary>
    public static IReadOnlyList<string> PlaceholdersIn(string? text) =>
        string.IsNullOrEmpty(text) ? [] : [.. Placeholder().Matches(text).Select(m => m.Groups[1].Value).Distinct(StringComparer.Ordinal)];

    /// <summary>The placeholders of a text that are not one of <see cref="Known"/>.</summary>
    public static IReadOnlyList<string> UnknownIn(string? text) => [.. PlaceholdersIn(text).Where(p => !Known.Contains(p, StringComparer.Ordinal))];

    /// <summary>
    /// Replaces the known placeholders. A known placeholder with no value becomes empty and is listed in <c>Empty</c>; an unknown one stays as typed so the author sees it.
    /// </summary>
    public static (string Text, IReadOnlyList<string> Empty) Render(string? body, IReadOnlyDictionary<string, string?> values)
    {
        var empty = new List<string>();
        var text = Placeholder().Replace(body ?? string.Empty, m =>
        {
            var key = m.Groups[1].Value;
            if (!Known.Contains(key, StringComparer.Ordinal))
            {
                return m.Value;
            }

            var value = values.GetValueOrDefault(key);
            if (string.IsNullOrWhiteSpace(value))
            {
                if (!empty.Contains(key))
                {
                    empty.Add(key);
                }

                return string.Empty;
            }

            return value;
        });
        return (text, empty);
    }
}

/// <summary>Pure arithmetic of the support report, kept apart so it can be tested against hand-made numbers.</summary>
public static class TicketStats
{
    public static double? Median(IReadOnlyCollection<double> values)
    {
        if (values.Count == 0)
        {
            return null;
        }

        var sorted = values.Order().ToArray();
        var mid = sorted.Length / 2;
        return sorted.Length % 2 == 1 ? sorted[mid] : (sorted[mid - 1] + sorted[mid]) / 2d;
    }

    public static DurationStatsDto Duration(IReadOnlyCollection<double> minutes) =>
        new(minutes.Count, minutes.Count == 0 ? null : Math.Round(minutes.Average(), 2), Median(minutes) is { } m ? Math.Round(m, 2) : null);
}

/// <summary>Limits and small helpers shared by every way a ticket is written.</summary>
public static class TicketLimits
{
    public const int MaxSubject = 200;
    public const int MaxText = 4000;
    public const int MaxName = 120;
    public const int MaxEmail = 200;
    public const int MaxAttachments = 5;
    public const int BulkLimit = 50;

    /// <summary>The name shown to a customer for any staff member.</summary>
    public const string StaffAlias = "Juvo BD Support";

    public static Error Invalid(string field, string message) => Error.Validation(new Dictionary<string, string[]> { [field] = [message] });

    public static Error NotFound() => Error.NotFound(ErrorCodes.Ticket.NotFound, "Ticket not found.");

    public static bool LooksLikeEmail(string email) => email.Length <= MaxEmail && Regex.IsMatch(email, @"^[^@\s]+@[^@\s]+\.[^@\s]+$", RegexOptions.None, TimeSpan.FromMilliseconds(100));

    /// <summary>Checks subject, message and category. Returns the cleaned values, or the first problem.</summary>
    public static Result<(string Subject, string Message, string Category)> Text(string? subject, string? message, string? category)
    {
        var s = subject?.Trim();
        var m = message?.Trim();
        if (string.IsNullOrEmpty(s) || s.Length > MaxSubject)
        {
            return Invalid("subject", $"Write a subject of up to {MaxSubject} characters.");
        }

        if (string.IsNullOrEmpty(m) || m.Length > MaxText)
        {
            return Invalid("message", $"Write a message of up to {MaxText} characters.");
        }

        var c = string.IsNullOrWhiteSpace(category) ? "other" : category.Trim().ToLowerInvariant();
        return TicketCategories.IsValid(c) ? (s, m, c) : Invalid("category", $"Choose one of: {string.Join(", ", TicketCategories.All)}.");
    }

    public static Result<string> ReplyText(string? text)
    {
        var t = text?.Trim();
        return string.IsNullOrEmpty(t) || t.Length > MaxText ? Invalid("text", $"Write between 1 and {MaxText} characters.") : t;
    }
}

/// <summary>The order a ticket is tied to, as far as a ticket needs to know it.</summary>
public sealed record TicketOrderRef(Guid Id, string Number, Guid BranchId, Guid CustomerId, string Mobile, string? CustomerName);

/// <summary>What the callers hand over to open a ticket.</summary>
public sealed record OpenTicketInput(
    TicketChannel Channel, TicketPriority Priority, string Subject, string Message, string Category, string Mobile, string? Name, string? Email, Guid? CustomerId, TicketOrderRef? Order,
    Guid? AssigneeId, string? AssigneeName, Who Author, IReadOnlyList<StoredFile> Files, bool RateLimited);

/// <summary>
/// The rules every ticket follows whoever writes it: the numbering, the due times by priority, the per-mobile rate limit, the state machine with its reopen window, and the
/// read models. Methods change tracked entities; the caller saves (inside its transaction when it opens a ticket, so a number is never lost).
/// </summary>
public sealed class TicketCore(IApplicationDbContext db, NumberAllocator numbers, OpsSettingsService settings, TimeProvider clock)
{
    public async Task<Error?> CheckRateAsync(string mobile, CancellationToken cancellationToken)
    {
        var s = await settings.ReadAsync(cancellationToken);
        var since = clock.GetUtcNow().AddHours(-1);
        var recent = await db.Tickets.AsNoTracking().CountAsync(t => t.CustomerMobile == mobile && t.CreatedAt > since, cancellationToken);
        return recent >= s.MaxTicketsPerHour
            ? new Error(ErrorCodes.Ticket.RateLimited, $"Too many requests from this number. Please wait before writing again (limit {s.MaxTicketsPerHour} per hour).", ErrorType.TooManyRequests)
            : null;
    }

    /// <summary>Numbers and creates the ticket with its first message. Call inside a transaction. The rate limit (when asked for) is checked first.</summary>
    public async Task<Result<Ticket>> OpenAsync(OpenTicketInput i, CancellationToken cancellationToken)
    {
        if (i.RateLimited && await CheckRateAsync(i.Mobile, cancellationToken) is { } limited)
        {
            return limited;
        }

        var now = clock.GetUtcNow();
        var s = await settings.ReadAsync(cancellationToken);
        var (responseDue, resolveDue) = s.DueTimes(i.Priority, now);
        var number = await numbers.NextGlobalAsync("TK", now, cancellationToken);
        var data = new NewTicket(number, i.Channel, i.Subject, i.Priority, i.Category, i.Mobile, i.Name, i.Email, i.CustomerId, i.Order?.Id, i.Order?.Number, null, null, i.Order?.BranchId, responseDue, resolveDue);
        var files = i.Files.Select(f => new NewTicketAttachment(f.Path, f.FileName, f.ContentType, f.Size)).ToList();
        var ticket = Ticket.Open(data, i.Message, i.Name, i.Author.Id, files, now);
        if (i.AssigneeId is { } assignee)
        {
            ticket.Assign(assignee, i.AssigneeName, now);
        }

        db.Tickets.Add(ticket);
        return ticket;
    }

    /// <summary>Moves the ticket if the state machine and the reopen window allow it. Null on success, otherwise the refusal.</summary>
    public async Task<Error?> MoveAsync(Ticket ticket, TicketStatus to, string? reason, Who who, CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        if (!TicketStateMachine.CanMove(ticket.Status, to))
        {
            return Error.Conflict(ErrorCodes.Ticket.InvalidTransition, $"A ticket that is {ticket.Status} cannot become {to}.");
        }

        if (TicketStateMachine.IsReopen(ticket.Status, to))
        {
            var days = (await settings.ReadAsync(cancellationToken)).ReopenDays;
            if (!ticket.WithinReopenWindow(now, days))
            {
                return Error.Conflict(ErrorCodes.Ticket.ReopenWindowExpired, $"This ticket was closed more than {days} days ago and cannot be reopened. Please write a new request.");
            }
        }

        ticket.MoveTo(to, string.IsNullOrWhiteSpace(reason) ? null : reason.Trim(), who.Id, who.Name, now);
        return null;
    }

    public Task<Ticket?> LoadAsync(Guid id, CancellationToken cancellationToken) =>
        db.Tickets.Include(t => t.Messages).ThenInclude(m => m.Attachments).Include(t => t.History).FirstOrDefaultAsync(t => t.Id == id, cancellationToken);

    public static IReadOnlyList<TicketAttachmentDto> Files(TicketMessage m, AttachmentStore store) =>
        [.. m.Attachments.OrderBy(a => a.CreatedAt).ThenBy(a => a.FileName).Select(a => new TicketAttachmentDto(a.Id, a.FileName, a.ContentType, a.Size, store.UrlOf(a.Path)))];

    public static bool Overdue(Ticket t, DateTimeOffset now) => t.IsOverdue(now);
}
