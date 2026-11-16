using Juvo.Application.Abstractions;
using Juvo.Application.Operations;
using Juvo.Application.Orders;
using Juvo.Domain.Support;
using Juvo.Shared.Contacts;
using Juvo.Shared.Pagination;
using Juvo.Shared.Results;
using Microsoft.EntityFrameworkCore;

namespace Juvo.Application.Support;

/// <summary>
/// The customer side of support (module Y, FR-Y01): the public contact form for guests and the signed-in customer's own tickets. A customer sees only their own tickets, never an internal
/// note, and never a staff member's name; anything that is not theirs looks like "not found".
/// </summary>
public sealed class MyTicketsService(
    IApplicationDbContext db, IUnitOfWork uow, ICurrentUser currentUser, TicketCore core, AttachmentStore attachments, OpsSettingsService opsSettings, TimeProvider clock)
{
    private Guid? CustomerId => currentUser is { IsAuthenticated: true, SubjectType: "customer", UserId: { } id } ? id : null;

    private static string FolderOf(Guid customerId) => $"tickets/c-{customerId:N}";

    // ---- Orders ------------------------------------------------------------------------------------------

    /// <summary>A customer may link only their own order; a guest only with the order number and the mobile that placed it. Anything else is a mismatch (no hint whether the order exists).</summary>
    private async Task<Result<TicketOrderRef>> OrderAsync(string orderNumber, Guid? customerId, string mobile, CancellationToken cancellationToken)
    {
        var number = orderNumber.Trim().ToUpperInvariant();
        var found = await db.Orders.IgnoreQueryFilters(["BranchScope"]).AsNoTracking().Where(o => o.Number == number && !o.IsDeleted)
            .Select(o => new TicketOrderRef(o.Id, o.Number, o.BranchId, o.CustomerId, o.CustomerMobile, o.CustomerName)).FirstOrDefaultAsync(cancellationToken);
        var mine = found is not null && (customerId is { } id ? found.CustomerId == id : found.Mobile == mobile);
        return mine ? found! : Error.Failure(ErrorCodes.Ticket.OrderMismatch, "This order was not placed with your account or mobile number.");
    }

    // ---- The contact form --------------------------------------------------------------------------------

    public async Task<Result<ContactReceiptDto>> ContactAsync(ContactRequest r, CancellationToken cancellationToken)
    {
        var name = r.Name?.Trim();
        if (string.IsNullOrEmpty(name) || name.Length > TicketLimits.MaxName)
        {
            return TicketLimits.Invalid("name", $"Enter your name (up to {TicketLimits.MaxName} characters).");
        }

        var mobile = PhoneNumber.Normalize(r.Mobile);
        if (mobile is null)
        {
            return TicketLimits.Invalid("mobile", "Enter a valid mobile number (for example 01712345678).");
        }

        var email = string.IsNullOrWhiteSpace(r.Email) ? null : r.Email.Trim();
        if (email is not null && !TicketLimits.LooksLikeEmail(email))
        {
            return TicketLimits.Invalid("email", "Check the email address.");
        }

        var text = TicketLimits.Text(r.Subject, r.Message, r.Category);
        if (text.IsFailure)
        {
            return text.Error!;
        }

        TicketOrderRef? order = null;
        if (!string.IsNullOrWhiteSpace(r.OrderNumber))
        {
            var found = await OrderAsync(r.OrderNumber, null, mobile, cancellationToken);
            if (found.IsFailure)
            {
                return found.Error!;
            }

            order = found.Value;
        }

        return await uow.ExecuteInTransactionAsync<Result<ContactReceiptDto>>(async ct =>
        {
            var opened = await core.OpenAsync(new OpenTicketInput(TicketChannel.Web, TicketPriority.Normal, text.Value.Subject, text.Value.Message, text.Value.Category, mobile, name, email, null, order, null, null,
                new Who(null, name), [], RateLimited: true), ct);
            if (opened.IsFailure)
            {
                return opened.Error!;
            }

            await db.SaveChangesAsync(ct);
            return new ContactReceiptDto(opened.Value.Number);
        }, cancellationToken);
    }

    // ---- My tickets --------------------------------------------------------------------------------------

    public async Task<Result<TicketUploadDto>> UploadAsync(Stream content, string? fileName, CancellationToken cancellationToken)
    {
        if (CustomerId is not { } id)
        {
            return Error.Unauthorized();
        }

        var saved = await attachments.SaveAsync(FolderOf(id), content, fileName, ErrorCodes.Ticket.AttachmentInvalid, cancellationToken);
        return saved.IsFailure ? saved.Error! : new TicketUploadDto(saved.Value.Path, saved.Value.FileName, saved.Value.ContentType, saved.Value.Size, saved.Value.Url);
    }

    public async Task<Result<PagedResult<MyTicketListItemDto>>> ListAsync(PageRequest request, TicketStatus? status, CancellationToken cancellationToken)
    {
        if (CustomerId is not { } id)
        {
            return Error.Unauthorized();
        }

        var query = db.Tickets.AsNoTracking().Where(t => t.CustomerId == id);
        if (status is { } s)
        {
            query = query.Where(t => t.Status == s);
        }

        if (!string.IsNullOrWhiteSpace(request.Search))
        {
            var term = request.Search.Trim();
            query = query.Where(t => t.Number.Contains(term) || t.Subject.Contains(term) || (t.OrderNumber != null && t.OrderNumber.Contains(term)));
        }

        var page = request.Normalized;
        var total = await query.CountAsync(cancellationToken);
        var rows = await query.OrderByDescending(t => t.LastActivityAt).ThenByDescending(t => t.CreatedAt).Skip(page.Skip).Take(page.PageSize).ToListAsync(cancellationToken);
        var items = rows.Select(t => new MyTicketListItemDto(t.Id, t.Number, t.Subject, t.Status, t.Category, t.OrderNumber, t.CreatedAt, t.LastActivityAt)).ToList();
        return new PagedResult<MyTicketListItemDto>(items, total, page.Page, page.PageSize);
    }

    public async Task<Result<MyTicketDto>> CreateAsync(CreateMyTicketRequest r, CancellationToken cancellationToken)
    {
        if (CustomerId is not { } id)
        {
            return Error.Unauthorized();
        }

        var customer = await db.Customers.AsNoTracking().Where(c => c.Id == id).Select(c => new { c.Mobile, c.FullName, c.Email }).FirstOrDefaultAsync(cancellationToken);
        if (customer is null)
        {
            return Error.Unauthorized();
        }

        var text = TicketLimits.Text(r.Subject, r.Message, r.Category);
        if (text.IsFailure)
        {
            return text.Error!;
        }

        TicketOrderRef? order = null;
        if (!string.IsNullOrWhiteSpace(r.OrderNumber))
        {
            var found = await OrderAsync(r.OrderNumber, id, customer.Mobile, cancellationToken);
            if (found.IsFailure)
            {
                return found.Error!;
            }

            order = found.Value;
        }

        var files = await attachments.ResolveAsync(FolderOf(id), r.AttachmentPaths, ErrorCodes.Ticket.AttachmentInvalid, TicketLimits.MaxAttachments, cancellationToken);
        if (files.IsFailure)
        {
            return files.Error!;
        }

        return await uow.ExecuteInTransactionAsync<Result<MyTicketDto>>(async ct =>
        {
            var opened = await core.OpenAsync(new OpenTicketInput(TicketChannel.Web, TicketPriority.Normal, text.Value.Subject, text.Value.Message, text.Value.Category, customer.Mobile, customer.FullName, customer.Email, id,
                order, null, null, Who.Customer(id, customer.FullName), files.Value, RateLimited: true), ct);
            if (opened.IsFailure)
            {
                return opened.Error!;
            }

            await db.SaveChangesAsync(ct);
            return await ViewAsync(opened.Value, ct);
        }, cancellationToken);
    }

    private async Task<Ticket?> MineAsync(Guid ticketId, Guid customerId, CancellationToken cancellationToken)
    {
        var ticket = await core.LoadAsync(ticketId, cancellationToken);
        return ticket is not null && ticket.CustomerId == customerId ? ticket : null;
    }

    public async Task<Result<MyTicketDto>> GetAsync(Guid ticketId, CancellationToken cancellationToken)
    {
        if (CustomerId is not { } id)
        {
            return Error.Unauthorized();
        }

        return await MineAsync(ticketId, id, cancellationToken) is { } ticket ? await ViewAsync(ticket, cancellationToken) : TicketLimits.NotFound();
    }

    private async Task<MyTicketDto> ViewAsync(Ticket t, CancellationToken cancellationToken)
    {
        var days = (await opsSettings.ReadAsync(cancellationToken)).ReopenDays;
        var now = clock.GetUtcNow();
        var reopenable = t.Status is TicketStatus.Resolved or TicketStatus.Closed && t.WithinReopenWindow(now, days);
        var messages = t.Messages.Where(m => m.Kind != TicketMessageKind.Internal).OrderBy(m => m.At).ThenBy(m => m.Id)
            .Select(m => new TicketMessageDto(m.Id, m.Kind, m.Text, null, m.Kind == TicketMessageKind.Staff ? TicketLimits.StaffAlias : m.AuthorName, m.At, TicketCore.Files(m, attachments))).ToList();
        return new MyTicketDto(t.Id, t.Number, t.Subject, t.Status, t.Category, t.OrderNumber, t.CreatedAt, t.LastActivityAt, CanReply: t.Status != TicketStatus.Closed && (t.Status != TicketStatus.Resolved || reopenable),
            CanClose: t.Status != TicketStatus.Closed, CanReopen: reopenable, messages);
    }

    public async Task<Result<MyTicketDto>> ReplyAsync(Guid ticketId, CustomerReplyRequest r, CancellationToken cancellationToken)
    {
        if (CustomerId is not { } id)
        {
            return Error.Unauthorized();
        }

        var text = TicketLimits.ReplyText(r.Text);
        if (text.IsFailure)
        {
            return text.Error!;
        }

        var files = await attachments.ResolveAsync(FolderOf(id), r.AttachmentPaths, ErrorCodes.Ticket.AttachmentInvalid, TicketLimits.MaxAttachments, cancellationToken);
        if (files.IsFailure)
        {
            return files.Error!;
        }

        var ticket = await MineAsync(ticketId, id, cancellationToken);
        if (ticket is null)
        {
            return TicketLimits.NotFound();
        }

        var who = Who.Customer(id, ticket.CustomerName);
        if (ticket.Status == TicketStatus.Closed)
        {
            return Error.Conflict(ErrorCodes.Ticket.ClosedTicket, "This request is closed. Reopen it to write again, or send a new request.");
        }

        var wasResolved = ticket.Status == TicketStatus.Resolved;
        if (wasResolved && await core.MoveAsync(ticket, TicketStatus.Open, "The customer replied", who, cancellationToken) is { } expired)
        {
            return expired;
        }

        var waiting = ticket.Status == TicketStatus.Waiting;
        var now = clock.GetUtcNow();
        ticket.AddMessage(TicketMessageKind.Customer, text.Value, id, ticket.CustomerName ?? "Customer", files.Value.Select(f => new NewTicketAttachment(f.Path, f.FileName, f.ContentType, f.Size)).ToList(), now);
        if (waiting && await core.MoveAsync(ticket, TicketStatus.InProgress, "The customer replied", who, cancellationToken) is { } refusal)
        {
            return refusal;
        }

        await db.SaveChangesAsync(cancellationToken);
        return await ViewAsync(ticket, cancellationToken);
    }

    public async Task<Result<MyTicketDto>> CloseAsync(Guid ticketId, CancellationToken cancellationToken) =>
        await MoveAsync(ticketId, TicketStatus.Closed, "Closed by the customer", cancellationToken);

    public async Task<Result<MyTicketDto>> ReopenAsync(Guid ticketId, CancellationToken cancellationToken) =>
        await MoveAsync(ticketId, TicketStatus.Open, "Reopened by the customer", cancellationToken);

    private async Task<Result<MyTicketDto>> MoveAsync(Guid ticketId, TicketStatus to, string reason, CancellationToken cancellationToken)
    {
        if (CustomerId is not { } id)
        {
            return Error.Unauthorized();
        }

        var ticket = await MineAsync(ticketId, id, cancellationToken);
        if (ticket is null)
        {
            return TicketLimits.NotFound();
        }

        // A customer may only reopen a ticket that is finished; the state machine alone would also let them "reopen" one that is being worked on.
        if (to == TicketStatus.Open && ticket.Status is not (TicketStatus.Resolved or TicketStatus.Closed))
        {
            return Error.Conflict(ErrorCodes.Ticket.InvalidTransition, $"A ticket that is {ticket.Status} cannot be reopened.");
        }

        if (await core.MoveAsync(ticket, to, reason, Who.Customer(id, ticket.CustomerName), cancellationToken) is { } refusal)
        {
            return refusal;
        }

        await db.SaveChangesAsync(cancellationToken);
        return await ViewAsync(ticket, cancellationToken);
    }
}
