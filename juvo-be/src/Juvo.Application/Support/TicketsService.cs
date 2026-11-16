using Juvo.Application.Abstractions;
using Juvo.Application.Identity;
using Juvo.Application.Inventory;
using Juvo.Application.Operations;
using Juvo.Application.Orders;
using Juvo.Domain.Support;
using Juvo.Shared.Contacts;
using Juvo.Shared.Pagination;
using Juvo.Shared.Results;
using Juvo.Shared.Security;
using Microsoft.EntityFrameworkCore;

namespace Juvo.Application.Support;

/// <summary>
/// Support tickets for staff (module Y, FR-Y01..FR-Y03): list, read, open for a customer, reply or note, assign, change priority and status, close, reopen and bulk actions. A ticket tied to
/// an order belongs to the order's branch and is proven there (another branch's ticket looks like "not found"); a ticket without an order is global and is open to every staff member
/// who holds the permission in any assignment.
/// </summary>
public sealed class TicketsService(
    IApplicationDbContext db, IUnitOfWork uow, InventoryAccess access, OrderAccess orderAccess, TicketCore core, AttachmentStore attachments, AccessService accessService, OpsSettingsService opsSettings,
    TimeProvider clock)
{
    private const string Folder = "tickets/staff";

    private static readonly IReadOnlySet<string> SortFields = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "created", "activity", "priority", "status", "number", "due" };

    // ---- Access ------------------------------------------------------------------------------------------

    /// <summary>Null when the actor holds <paramref name="permission"/> for the ticket (in its branch, or anywhere for a global ticket); otherwise the refusal.</summary>
    private async Task<Error?> CheckAsync(Ums.Actor actor, Ticket ticket, string permission, CancellationToken cancellationToken)
    {
        if (ticket.BranchId is { } branchId)
        {
            var refusal = await access.CheckAsync(actor, branchId, permission, cancellationToken);
            return refusal is { Type: ErrorType.NotFound } ? TicketLimits.NotFound() : refusal;
        }

        return actor.IsGlobal || actor.Access.HasAnywhere(permission)
            ? null
            : Error.Forbidden(ErrorCodes.Auth.Forbidden, "You do not hold this permission.");
    }

    /// <summary>The tickets the actor may see: every ticket for global staff, otherwise those of their branches plus the global ones.</summary>
    private IQueryable<Ticket> Visible(Ums.Actor actor, string permission)
    {
        var branches = InventoryAccess.VisibleBranches(actor, permission);
        var query = db.Tickets.AsNoTracking().AsQueryable();
        if (branches is null)
        {
            return query;
        }

        var global = actor.Access.HasAnywhere(permission);
        return query.Where(t => (t.BranchId != null && branches.Contains(t.BranchId.Value)) || (global && t.BranchId == null));
    }

    // ---- Reading -----------------------------------------------------------------------------------------

    public async Task<Result<PagedResult<TicketListItemDto>>> ListAsync(PageRequest request, TicketListQuery filter, CancellationToken cancellationToken)
    {
        var actor = await access.ActorAsync(cancellationToken);
        if (actor.IsFailure)
        {
            return actor.Error!;
        }

        var query = Visible(actor.Value, Permissions.Support.TicketView);
        var now = clock.GetUtcNow();
        if (filter.Status is { } status)
        {
            query = query.Where(t => t.Status == status);
        }

        if (filter.Active == true)
        {
            query = query.Where(t => t.Status != TicketStatus.Resolved && t.Status != TicketStatus.Closed);
        }

        if (filter.Priority is { } priority)
        {
            query = query.Where(t => t.Priority == priority);
        }

        if (filter.AssigneeId is { } assignee)
        {
            query = query.Where(t => t.AssigneeId == assignee);
        }

        if (filter.Unassigned == true)
        {
            query = query.Where(t => t.AssigneeId == null);
        }

        if (filter.BranchId is { } branchId)
        {
            query = query.Where(t => t.BranchId == branchId);
        }

        if (!string.IsNullOrWhiteSpace(filter.OrderNumber))
        {
            var number = filter.OrderNumber.Trim().ToUpperInvariant();
            query = query.Where(t => t.OrderNumber == number);
        }

        if (filter.Channel is { } channel)
        {
            query = query.Where(t => t.Channel == channel);
        }

        if (!string.IsNullOrWhiteSpace(filter.Category))
        {
            var category = filter.Category.Trim().ToLowerInvariant();
            query = query.Where(t => t.Category == category);
        }

        if (filter.From is { } from)
        {
            var start = Ops.StartOfDay(from);
            query = query.Where(t => t.CreatedAt >= start);
        }

        if (filter.To is { } to)
        {
            var end = Ops.StartOfDay(to.AddDays(1));
            query = query.Where(t => t.CreatedAt < end);
        }

        if (filter.Overdue == true)
        {
            query = query.Where(t => (t.Status == TicketStatus.Open || t.Status == TicketStatus.InProgress) && ((t.FirstResponseAt == null && now > t.ResponseDueAt) || now > t.ResolveDueAt));
        }

        if (!string.IsNullOrWhiteSpace(request.Search))
        {
            var term = request.Search.Trim();
            var mobile = PhoneNumber.Normalize(term);
            query = query.Where(t => t.Number.Contains(term) || t.Subject.Contains(term) || (t.CustomerName != null && t.CustomerName.Contains(term)) || t.CustomerMobile.Contains(term)
                || (mobile != null && t.CustomerMobile == mobile));
        }

        var sort = request.ParseSort(SortFields) is { Count: > 0 } f ? f[0] : null;
        query = (sort?.Name.ToLowerInvariant(), sort?.Descending ?? true) switch
        {
            ("created", true) => query.OrderByDescending(t => t.CreatedAt),
            ("created", false) => query.OrderBy(t => t.CreatedAt),
            ("priority", true) => query.OrderByDescending(t => t.Priority).ThenBy(t => t.CreatedAt),
            ("priority", false) => query.OrderBy(t => t.Priority).ThenBy(t => t.CreatedAt),
            ("status", true) => query.OrderByDescending(t => t.Status).ThenByDescending(t => t.LastActivityAt),
            ("status", false) => query.OrderBy(t => t.Status).ThenByDescending(t => t.LastActivityAt),
            ("number", true) => query.OrderByDescending(t => t.Number),
            ("number", false) => query.OrderBy(t => t.Number),
            ("due", true) => query.OrderByDescending(t => t.ResolveDueAt),
            ("due", false) => query.OrderBy(t => t.ResolveDueAt),
            (_, false) => query.OrderBy(t => t.LastActivityAt),
            _ => query.OrderByDescending(t => t.LastActivityAt).ThenByDescending(t => t.CreatedAt),
        };

        var page = request.Normalized;
        var total = await query.CountAsync(cancellationToken);
        var rows = await (from t in query.Skip(page.Skip).Take(page.PageSize)
                          join b in db.Branches.AsNoTracking() on t.BranchId equals b.Id into branches
                          from b in branches.DefaultIfEmpty()
                          select new { Ticket = t, BranchCode = b != null ? b.Code : null, Messages = t.Messages.Count }).ToListAsync(cancellationToken);
        var items = rows.Select(x => ToListItem(x.Ticket, x.BranchCode, x.Messages, now)).ToList();
        return new PagedResult<TicketListItemDto>(items, total, page.Page, page.PageSize);
    }

    public async Task<Result<TicketSummaryDto>> SummaryAsync(CancellationToken cancellationToken)
    {
        var actor = await access.ActorAsync(cancellationToken);
        if (actor.IsFailure)
        {
            return actor.Error!;
        }

        var scope = Visible(actor.Value, Permissions.Support.TicketView);
        var now = clock.GetUtcNow();
        var me = actor.Value.Id;
        var counts = await scope.GroupBy(t => t.Status).Select(g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.Count, cancellationToken);
        var unassigned = await scope.CountAsync(t => t.AssigneeId == null && t.Status != TicketStatus.Resolved && t.Status != TicketStatus.Closed, cancellationToken);
        var mine = await scope.CountAsync(t => t.AssigneeId == me && t.Status != TicketStatus.Resolved && t.Status != TicketStatus.Closed, cancellationToken);
        var overdue = await scope.CountAsync(t => (t.Status == TicketStatus.Open || t.Status == TicketStatus.InProgress) && ((t.FirstResponseAt == null && now > t.ResponseDueAt) || now > t.ResolveDueAt), cancellationToken);
        var byStatus = Enum.GetValues<TicketStatus>().ToDictionary(s => s.ToString(), s => counts.GetValueOrDefault(s));
        return new TicketSummaryDto(byStatus, counts.Where(c => TicketStateMachine.IsActive(c.Key)).Sum(c => c.Value), unassigned, overdue, mine);
    }

    private static TicketListItemDto ToListItem(Ticket t, string? branchCode, int messages, DateTimeOffset now) =>
        new(t.Id, t.Number, t.Subject, t.Status, t.Priority, t.Channel, t.Category, t.CustomerMobile, t.CustomerName, t.CustomerId, t.OrderId, t.OrderNumber, t.BranchId, branchCode, t.AssigneeId, t.AssigneeName,
            t.CreatedAt, t.LastActivityAt, t.ResponseDueAt, t.ResolveDueAt, t.FirstResponseAt, t.IsOverdue(now), messages);

    public async Task<Result<TicketDetailDto>> GetAsync(Guid id, CancellationToken cancellationToken)
    {
        var actor = await access.ActorAsync(cancellationToken);
        if (actor.IsFailure)
        {
            return actor.Error!;
        }

        var ticket = await core.LoadAsync(id, cancellationToken);
        if (ticket is null)
        {
            return TicketLimits.NotFound();
        }

        return await CheckAsync(actor.Value, ticket, Permissions.Support.TicketView, cancellationToken) is { } refusal ? refusal : await DetailAsync(ticket, cancellationToken);
    }

    private async Task<TicketDetailDto> DetailAsync(Ticket t, CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        var branchCode = t.BranchId is { } b ? await db.Branches.AsNoTracking().Where(x => x.Id == b).Select(x => x.Code).FirstOrDefaultAsync(cancellationToken) : null;
        var days = (await opsSettings.ReadAsync(cancellationToken)).ReopenDays;
        var messages = t.Messages.OrderBy(m => m.At).ThenBy(m => m.Id)
            .Select(m => new TicketMessageDto(m.Id, m.Kind, m.Text, m.AuthorId, m.AuthorName, m.At, TicketCore.Files(m, attachments))).ToList();
        var history = t.History.OrderBy(h => h.At).ThenBy(h => h.Id).Select(h => new TicketHistoryDto(h.FromStatus, h.ToStatus, h.Reason, h.ActorName, h.At)).ToList();
        var canReopen = t.Status is TicketStatus.Resolved or TicketStatus.Closed && t.WithinReopenWindow(now, days);
        return new TicketDetailDto(t.Id, t.Number, t.Subject, t.Status, t.Priority, t.Channel, t.Category, t.CustomerMobile, t.CustomerName, t.CustomerEmail, t.CustomerId, t.OrderId, t.OrderNumber, t.BranchId, branchCode,
            t.AssigneeId, t.AssigneeName, t.CreatedAt, t.LastActivityAt, t.ResponseDueAt, t.ResolveDueAt, t.FirstResponseAt, t.ResolvedAt, t.ClosedAt, t.ReopenCount, t.IsOverdue(now), canReopen,
            [.. TicketStateMachine.NextFor(t.Status).Select(s => s.ToString())], messages, history);
    }

    // ---- Opening -----------------------------------------------------------------------------------------

    public async Task<Result<TicketUploadDto>> UploadAsync(Stream content, string? fileName, CancellationToken cancellationToken)
    {
        var saved = await attachments.SaveAsync(Folder, content, fileName, ErrorCodes.Ticket.AttachmentInvalid, cancellationToken);
        return saved.IsFailure ? saved.Error! : new TicketUploadDto(saved.Value.Path, saved.Value.FileName, saved.Value.ContentType, saved.Value.Size, saved.Value.Url);
    }

    public async Task<Result<TicketDetailDto>> CreateAsync(StaffCreateTicketRequest r, CancellationToken cancellationToken)
    {
        var actor = await access.ActorAsync(cancellationToken);
        if (actor.IsFailure)
        {
            return actor.Error!;
        }

        var text = TicketLimits.Text(r.Subject, r.Message, r.Category);
        if (text.IsFailure)
        {
            return text.Error!;
        }

        var channel = r.Channel ?? TicketChannel.Phone;
        var priority = r.Priority ?? TicketPriority.Normal;
        if (!Enum.IsDefined(channel) || !Enum.IsDefined(priority))
        {
            return TicketLimits.Invalid("channel", "Choose a valid channel and priority.");
        }

        var email = string.IsNullOrWhiteSpace(r.Email) ? null : r.Email.Trim();
        if ((email is not null && !TicketLimits.LooksLikeEmail(email)) || Ops.TooLong(r.CustomerName, TicketLimits.MaxName))
        {
            return TicketLimits.Invalid("email", "Check the email address and keep the name under 120 characters.");
        }

        var mobile = PhoneNumber.Normalize(r.Mobile);
        TicketOrderRef? order = null;
        if (!string.IsNullOrWhiteSpace(r.OrderNumber))
        {
            var number = r.OrderNumber.Trim().ToUpperInvariant();
            var found = await db.Orders.AsNoTracking().Where(o => o.Number == number && !o.IsDeleted)
                .Select(o => new TicketOrderRef(o.Id, o.Number, o.BranchId, o.CustomerId, o.CustomerMobile, o.CustomerName)).FirstOrDefaultAsync(cancellationToken);
            if (found is null)
            {
                return Error.NotFound(ErrorCodes.Ticket.OrderNotFound, "Order not found.");
            }

            if (await access.CheckAsync(actor.Value, found.BranchId, Permissions.Support.TicketCreate, cancellationToken) is { } refusal)
            {
                return refusal.Type == ErrorType.NotFound ? Error.NotFound(ErrorCodes.Ticket.OrderNotFound, "Order not found.") : refusal;
            }

            if (mobile is not null && mobile != found.Mobile)
            {
                return Error.Failure(ErrorCodes.Ticket.OrderMismatch, "The mobile number is not the one this order was placed with.");
            }

            order = found;
            mobile = found.Mobile;
        }
        else if (!actor.Value.IsGlobal && !actor.Value.Access.HasAnywhere(Permissions.Support.TicketCreate))
        {
            return Error.Forbidden(ErrorCodes.Auth.Forbidden, "You do not hold this permission.");
        }

        if (mobile is null)
        {
            return TicketLimits.Invalid("mobile", "Enter the customer's mobile number (for example 01712345678).");
        }

        var customer = await db.Customers.AsNoTracking().Where(c => c.Mobile == mobile).Select(c => new { c.Id, c.FullName, c.Email }).FirstOrDefaultAsync(cancellationToken);
        var name = Ops.Clean(r.CustomerName, TicketLimits.MaxName) ?? order?.CustomerName ?? customer?.FullName;
        email ??= customer?.Email;
        var branchForCheck = order?.BranchId;

        var assigneeName = (string?)null;
        if (r.AssigneeId is { } assigneeId)
        {
            if (!actor.Value.Can(Permissions.Support.TicketAssign, branchForCheck) && !(branchForCheck is null && actor.Value.Access.HasAnywhere(Permissions.Support.TicketAssign)))
            {
                return Error.Forbidden(ErrorCodes.Auth.Forbidden, "You do not hold the permission to assign tickets.");
            }

            var assignee = await FindAssigneeAsync(assigneeId, branchForCheck, cancellationToken);
            if (assignee.IsFailure)
            {
                return assignee.Error!;
            }

            assigneeName = assignee.Value;
        }

        var files = await attachments.ResolveAsync(Folder, r.AttachmentPaths, ErrorCodes.Ticket.AttachmentInvalid, TicketLimits.MaxAttachments, cancellationToken);
        if (files.IsFailure)
        {
            return files.Error!;
        }

        var who = orderAccess.WhoIs(actor.Value);
        return await uow.ExecuteInTransactionAsync<Result<TicketDetailDto>>(async ct =>
        {
            var opened = await core.OpenAsync(new OpenTicketInput(channel, priority, text.Value.Subject, text.Value.Message, text.Value.Category, mobile, name, email, order?.CustomerId ?? customer?.Id, order,
                r.AssigneeId, assigneeName, who, files.Value, RateLimited: false), ct);
            if (opened.IsFailure)
            {
                return opened.Error!;
            }

            await db.SaveChangesAsync(ct);
            return await DetailAsync(opened.Value, ct);
        }, cancellationToken);
    }

    // ---- Assignment --------------------------------------------------------------------------------------

    /// <summary>The assignee's display name when they are an active staff member who may view tickets of the branch (or anywhere for a global ticket).</summary>
    private async Task<Result<string>> FindAssigneeAsync(Guid assigneeId, Guid? branchId, CancellationToken cancellationToken)
    {
        var user = await db.Users.AsNoTracking().Where(u => u.Id == assigneeId && u.IsActive).Select(u => new { u.Id, u.FullName }).FirstOrDefaultAsync(cancellationToken);
        if (user is null)
        {
            return Error.NotFound(ErrorCodes.Ticket.AssigneeNotFound, "That staff member was not found.");
        }

        var theirs = await accessService.ComputeAsync(assigneeId, cancellationToken);
        var can = branchId is { } b ? theirs.Has(Permissions.Support.TicketView, b) : theirs.HasAnywhere(Permissions.Support.TicketView);
        return can ? user.FullName : Error.NotFound(ErrorCodes.Ticket.AssigneeNotFound, "That staff member cannot work on tickets of this branch.");
    }

    public async Task<Result<TicketDetailDto>> AssignAsync(Guid id, AssignTicketRequest r, CancellationToken cancellationToken) =>
        await RunAsync(id, Permissions.Support.TicketAssign, async (ticket, actor, who, ct) =>
        {
            string? name = null;
            if (r.AssigneeId is { } assigneeId)
            {
                var assignee = await FindAssigneeAsync(assigneeId, ticket.BranchId, ct);
                if (assignee.IsFailure)
                {
                    return assignee.Error;
                }

                name = assignee.Value;
            }

            var now = clock.GetUtcNow();
            ticket.Assign(r.AssigneeId, name, now);
            ticket.AddMessage(TicketMessageKind.Internal, r.AssigneeId is null ? "Unassigned." : $"Assigned to {name}.", who.Id, who.Name, [], now);
            return null;
        }, cancellationToken);

    public async Task<Result<TicketDetailDto>> PriorityAsync(Guid id, ChangePriorityRequest r, CancellationToken cancellationToken) =>
        await RunAsync(id, Permissions.Support.TicketAssign, async (ticket, actor, who, ct) =>
        {
            if (!Enum.IsDefined(r.Priority))
            {
                return TicketLimits.Invalid("priority", "Choose Low, Normal, High or Urgent.");
            }

            var s = await opsSettings.ReadAsync(ct);
            var (responseDue, resolveDue) = s.DueTimes(r.Priority, ticket.CreatedAt);
            var now = clock.GetUtcNow();
            var before = ticket.Priority;
            ticket.Reprioritize(r.Priority, responseDue, resolveDue, now);
            ticket.AddMessage(TicketMessageKind.Internal, $"Priority changed from {before} to {r.Priority}.", who.Id, who.Name, [], now);
            return null;
        }, cancellationToken);

    // ---- Replies and status ------------------------------------------------------------------------------

    public async Task<Result<TicketDetailDto>> ReplyAsync(Guid id, StaffReplyRequest r, CancellationToken cancellationToken)
    {
        var text = TicketLimits.ReplyText(r.Text);
        if (text.IsFailure)
        {
            return text.Error!;
        }

        var files = await attachments.ResolveAsync(Folder, r.AttachmentPaths, ErrorCodes.Ticket.AttachmentInvalid, TicketLimits.MaxAttachments, cancellationToken);
        if (files.IsFailure)
        {
            return files.Error!;
        }

        var newFiles = files.Value.Select(f => new NewTicketAttachment(f.Path, f.FileName, f.ContentType, f.Size)).ToList();
        return await RunAsync(id, Permissions.Support.TicketReply, async (ticket, actor, who, ct) =>
        {
            var now = clock.GetUtcNow();
            if (!r.Internal && ticket.Status == TicketStatus.Closed)
            {
                return Error.Conflict(ErrorCodes.Ticket.ClosedTicket, "This ticket is closed. Reopen it to write to the customer.");
            }

            if (r.SetStatus is { } target && target != ticket.Status && StatusPermission(ticket.Status, target) != Permissions.Support.TicketReply
                && await CheckAsync(actor, ticket, StatusPermission(ticket.Status, target), ct) is { } refusal)
            {
                return refusal;
            }

            ticket.AddMessage(r.Internal ? TicketMessageKind.Internal : TicketMessageKind.Staff, text.Value, who.Id, who.Name, newFiles, now);
            if (!r.Internal && ticket.Status == TicketStatus.Open && r.SetStatus is null)
            {
                return await core.MoveAsync(ticket, TicketStatus.InProgress, "Staff replied", who, ct);
            }

            if (r.SetStatus is { } to && to != ticket.Status)
            {
                return await core.MoveAsync(ticket, to, "Set with a reply", who, ct);
            }

            return null;
        }, cancellationToken);
    }

    /// <summary>Closing a ticket, or reopening one, needs the close permission; every other move needs the reply permission.</summary>
    private static string StatusPermission(TicketStatus from, TicketStatus to) =>
        to == TicketStatus.Closed || TicketStateMachine.IsReopen(from, to) ? Permissions.Support.TicketClose : Permissions.Support.TicketReply;

    public async Task<Result<TicketDetailDto>> ChangeStatusAsync(Guid id, ChangeStatusRequest r, CancellationToken cancellationToken)
    {
        if (!Enum.IsDefined(r.Status))
        {
            return TicketLimits.Invalid("status", "Choose a valid status.");
        }

        var actor = await access.ActorAsync(cancellationToken);
        if (actor.IsFailure)
        {
            return actor.Error!;
        }

        var current = await db.Tickets.AsNoTracking().Where(t => t.Id == id).Select(t => (TicketStatus?)t.Status).FirstOrDefaultAsync(cancellationToken);
        if (current is null)
        {
            return TicketLimits.NotFound();
        }

        return await RunAsync(id, StatusPermission(current.Value, r.Status), (ticket, _, who, ct) => core.MoveAsync(ticket, r.Status, r.Reason, who, ct), cancellationToken);
    }

    public Task<Result<TicketDetailDto>> ResolveAsync(Guid id, ReasonRequest r, CancellationToken cancellationToken) => ChangeStatusAsync(id, new ChangeStatusRequest(TicketStatus.Resolved, r.Reason), cancellationToken);

    public Task<Result<TicketDetailDto>> CloseAsync(Guid id, ReasonRequest r, CancellationToken cancellationToken) => ChangeStatusAsync(id, new ChangeStatusRequest(TicketStatus.Closed, r.Reason), cancellationToken);

    public Task<Result<TicketDetailDto>> ReopenAsync(Guid id, ReasonRequest r, CancellationToken cancellationToken) => ChangeStatusAsync(id, new ChangeStatusRequest(TicketStatus.Open, r.Reason ?? "Reopened by staff"), cancellationToken);

    // ---- Bulk --------------------------------------------------------------------------------------------

    public async Task<Result<BulkResultDto>> BulkAsync(BulkTicketsRequest r, CancellationToken cancellationToken)
    {
        var ids = (r.Ids ?? []).Distinct().ToList();
        if (ids.Count == 0)
        {
            return TicketLimits.Invalid("ids", "Choose at least one ticket.");
        }

        if (ids.Count > TicketLimits.BulkLimit)
        {
            return Error.Failure(ErrorCodes.Ticket.BulkLimit, $"At most {TicketLimits.BulkLimit} tickets can be changed at once.");
        }

        var action = r.Action?.Trim().ToLowerInvariant();
        if (action is not ("assign" or "close"))
        {
            return TicketLimits.Invalid("action", "The action is assign or close.");
        }

        var items = new List<BulkItemDto>();
        foreach (var id in ids)
        {
            var one = action == "assign"
                ? await AssignAsync(id, new AssignTicketRequest(r.AssigneeId), cancellationToken)
                : await CloseAsync(id, new ReasonRequest(r.Reason ?? "Closed in bulk"), cancellationToken);
            items.Add(one.IsSuccess ? new BulkItemDto(id, true, null, null) : new BulkItemDto(id, false, one.Error!.Code, one.Error.Message));
        }

        var ok = items.Count(i => i.Ok);
        return new BulkResultDto(items.Count, ok, items.Count - ok, items);
    }

    // ---- Plumbing ----------------------------------------------------------------------------------------

    /// <summary>Loads the ticket, proves the permission for it, runs the change, saves and answers the detail.</summary>
    private async Task<Result<TicketDetailDto>> RunAsync(Guid id, string permission, Func<Ticket, Ums.Actor, Who, CancellationToken, Task<Error?>> change, CancellationToken cancellationToken)
    {
        var actor = await access.ActorAsync(cancellationToken);
        if (actor.IsFailure)
        {
            return actor.Error!;
        }

        var ticket = await core.LoadAsync(id, cancellationToken);
        if (ticket is null)
        {
            return TicketLimits.NotFound();
        }

        if (await CheckAsync(actor.Value, ticket, permission, cancellationToken) is { } refusal)
        {
            return refusal;
        }

        if (await change(ticket, actor.Value, orderAccess.WhoIs(actor.Value), cancellationToken) is { } error)
        {
            return error;
        }

        await db.SaveChangesAsync(cancellationToken);
        return await DetailAsync(ticket, cancellationToken);
    }
}
