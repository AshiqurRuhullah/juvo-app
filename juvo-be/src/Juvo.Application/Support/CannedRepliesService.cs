using Juvo.Application.Abstractions;
using Juvo.Domain.Support;
using Juvo.Shared.Results;
using Microsoft.EntityFrameworkCore;

namespace Juvo.Application.Support;

/// <summary>
/// Canned replies (module Y, FR-Y03): ready-made answers per language that staff insert into a ticket. A text may use <c>{customerName}</c>, <c>{orderNumber}</c> and <c>{ticketNumber}</c>;
/// anything else in curly braces is refused when saving so a typing mistake never reaches a customer.
/// </summary>
public sealed class CannedRepliesService(IApplicationDbContext db, TicketsService tickets)
{
    private static readonly string[] Languages = ["en", "bn"];

    private static CannedReplyDto ToDto(CannedReply c) => new(c.Id, c.Title, c.Body, c.Language, c.Category, c.IsActive, CannedReplyTemplate.PlaceholdersIn(c.Body));

    private static Error NotFound() => Error.NotFound(ErrorCodes.Ticket.CannedReplyNotFound, "Canned reply not found.");

    public async Task<Result<IReadOnlyList<CannedReplyDto>>> ListAsync(CannedReplyQuery filter, CancellationToken cancellationToken)
    {
        var query = db.CannedReplies.AsNoTracking().AsQueryable();
        if (!filter.IncludeInactive)
        {
            query = query.Where(c => c.IsActive);
        }

        if (!string.IsNullOrWhiteSpace(filter.Language))
        {
            var language = filter.Language.Trim().ToLowerInvariant();
            query = query.Where(c => c.Language == language);
        }

        if (!string.IsNullOrWhiteSpace(filter.Category))
        {
            var category = filter.Category.Trim();
            query = query.Where(c => c.Category == category);
        }

        var rows = await query.OrderBy(c => c.Language).ThenBy(c => c.Title).ToListAsync(cancellationToken);
        return rows.Select(ToDto).ToList();
    }

    private async Task<Error?> ValidateAsync(Guid? id, SaveCannedReplyRequest r, CancellationToken cancellationToken)
    {
        var title = r.Title?.Trim();
        var body = r.Body?.Trim();
        var language = r.Language?.Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(title) || title.Length > 120)
        {
            return TicketLimits.Invalid("title", "Give the reply a title (up to 120 characters).");
        }

        if (string.IsNullOrEmpty(body) || body.Length > 2000)
        {
            return TicketLimits.Invalid("body", "Write the reply (up to 2000 characters).");
        }

        if (language is null || !Languages.Contains(language))
        {
            return TicketLimits.Invalid("language", "The language is en or bn.");
        }

        if (r.Category is { Length: > 60 })
        {
            return TicketLimits.Invalid("category", "The category can have 60 characters.");
        }

        if (CannedReplyTemplate.UnknownIn(body) is { Count: > 0 } unknown)
        {
            return TicketLimits.Invalid("body", $"Unknown placeholder {{{unknown[0]}}}. Use {string.Join(", ", CannedReplyTemplate.Known.Select(k => "{" + k + "}"))}.");
        }

        return await db.CannedReplies.AnyAsync(c => c.Language == language && c.Title == title && c.Id != id, cancellationToken)
            ? Error.Conflict(ErrorCodes.General.Conflict, "A reply with this title already exists in this language.")
            : null;
    }

    public async Task<Result<CannedReplyDto>> CreateAsync(SaveCannedReplyRequest r, CancellationToken cancellationToken)
    {
        if (await ValidateAsync(null, r, cancellationToken) is { } error)
        {
            return error;
        }

        var reply = new CannedReply(r.Title!, r.Body!, r.Language!.Trim().ToLowerInvariant(), r.Category);
        if (r.IsActive == false)
        {
            reply.SetActive(false);
        }

        db.CannedReplies.Add(reply);
        await db.SaveChangesAsync(cancellationToken);
        return ToDto(reply);
    }

    public async Task<Result<CannedReplyDto>> UpdateAsync(Guid id, SaveCannedReplyRequest r, CancellationToken cancellationToken)
    {
        var reply = await db.CannedReplies.FirstOrDefaultAsync(c => c.Id == id, cancellationToken);
        if (reply is null)
        {
            return NotFound();
        }

        if (await ValidateAsync(id, r, cancellationToken) is { } error)
        {
            return error;
        }

        reply.Update(r.Title!, r.Body!, r.Language!.Trim().ToLowerInvariant(), r.Category);
        if (r.IsActive is { } active)
        {
            reply.SetActive(active);
        }

        await db.SaveChangesAsync(cancellationToken);
        return ToDto(reply);
    }

    public async Task<Result> DeleteAsync(Guid id, CancellationToken cancellationToken)
    {
        var reply = await db.CannedReplies.FirstOrDefaultAsync(c => c.Id == id, cancellationToken);
        if (reply is null)
        {
            return NotFound();
        }

        db.CannedReplies.Remove(reply);
        await db.SaveChangesAsync(cancellationToken);
        return Result.Success();
    }

    /// <summary>Fills the placeholders of a canned reply (or of the given text) from a ticket the caller may read.</summary>
    public async Task<Result<RenderedReplyDto>> RenderAsync(RenderCannedReplyRequest r, CancellationToken cancellationToken)
    {
        string? body = r.Body;
        if (r.CannedReplyId is { } id)
        {
            body = await db.CannedReplies.AsNoTracking().Where(c => c.Id == id).Select(c => c.Body).FirstOrDefaultAsync(cancellationToken);
            if (body is null)
            {
                return NotFound();
            }
        }

        if (string.IsNullOrWhiteSpace(body) || body.Length > 2000)
        {
            return TicketLimits.Invalid("body", "Send a canned reply id, or a text of up to 2000 characters.");
        }

        var values = new Dictionary<string, string?>(StringComparer.Ordinal);
        if (r.TicketId is { } ticketId)
        {
            var ticket = await tickets.GetAsync(ticketId, cancellationToken);
            if (ticket.IsFailure)
            {
                return ticket.Error!;
            }

            values["customerName"] = ticket.Value.CustomerName;
            values["orderNumber"] = ticket.Value.OrderNumber;
            values["ticketNumber"] = ticket.Value.Number;
        }

        var (text, empty) = CannedReplyTemplate.Render(body, values);
        return new RenderedReplyDto(text, CannedReplyTemplate.UnknownIn(body), empty);
    }
}
