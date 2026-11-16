using Juvo.Api.Configuration;
using Juvo.Api.Http;
using Juvo.Application.Support;
using Juvo.Domain.Support;
using Juvo.Shared.Pagination;
using Juvo.Shared.Security;
using Microsoft.AspNetCore.Mvc;

namespace Juvo.Api.Endpoints;

/// <summary>
/// Support tickets (module Y): staff work on tickets and canned replies and read the report, a signed-in customer follows their own tickets, and a guest writes through the public contact
/// form. Staff endpoints name their permission; the services prove it again in the ticket's branch, so another branch's ticket looks like "not found". A customer can never read an
/// internal note or another customer's ticket.
/// </summary>
internal static class SupportEndpoints
{
    public static WebApplication MapSupportEndpoints(this WebApplication app)
    {
        var v1 = app.MapApiV1();
        MapTickets(v1.MapGroup("/support/tickets").WithTags("Support tickets"));
        MapCannedReplies(v1.MapGroup("/support/canned-replies").WithTags("Canned replies"));

        v1.MapGet("/support/report", async (DateOnly? from, DateOnly? to, Guid? branchId, SupportReportService s, CancellationToken ct) => (await s.BuildAsync(from, to, branchId, ct)).ToHttpResult())
            .WithTags("Support report").RequirePermission(Permissions.Support.ReportView).Produces<SupportReportDto>()
            .WithSummary("Open tickets by status and priority, new and resolved per day, average and median first response and resolution time, tickets per category and per assignee, the oldest open ones. Days are Bangladesh days (default: the last 30). Without branchId it is the consolidated view of every branch you may report on.");

        MapMine(v1.MapGroup("/me/tickets").WithTags("Customer tickets").RequireAuthorization(Policies.Customer));

        v1.MapPost("/store/contact", async (ContactRequest r, MyTicketsService s, CancellationToken ct) => (await s.ContactAsync(r, ct)).ToHttpResult(v => Results.Created($"/api/v1/store/contact/{v.Number}", v)))
            .WithTags("Storefront").AllowAnonymous().RequireRateLimiting(RateLimitSettings.CheckoutPolicy).Produces<ContactReceiptDto>(StatusCodes.Status201Created)
            .WithSummary("The public contact form: name, mobile, optional email, subject and message (and an order number only together with the mobile that placed it). Answers the ticket number. Limited per client address and per mobile number per hour (TICKET.RATE_LIMITED).");
        return app;
    }

    private static void MapTickets(RouteGroupBuilder g)
    {
        g.MapGet("", async ([AsParameters] ListParams list, TicketStatus? status, TicketPriority? priority, Guid? assigneeId, Guid? branchId, string? orderNumber, TicketChannel? channel, string? category,
                    DateOnly? from, DateOnly? to, bool? unassigned, bool? overdue, bool? active, TicketsService s, CancellationToken ct) =>
                (await s.ListAsync(list.ToRequest(), new TicketListQuery(status, priority, assigneeId, branchId, orderNumber, channel, category, from, to, unassigned, overdue, active), ct)).ToHttpResult())
            .RequirePermission(Permissions.Support.TicketView).Produces<PagedResult<TicketListItemDto>>()
            .WithSummary("Tickets of the branches where you may view them, plus global ones, most recent activity first. Filters: status, priority, assignee, unassigned, branch, order number, channel, category, overdue, active, created from/to; search number, subject, customer name or mobile; sort created, activity, priority, status, number, due.");

        g.MapGet("/summary", async (TicketsService s, CancellationToken ct) => (await s.SummaryAsync(ct)).ToHttpResult())
            .RequirePermission(Permissions.Support.TicketView).Produces<TicketSummaryDto>().WithSummary("Counts per status, active, unassigned, overdue and assigned to me, over the tickets you may see.");

        g.MapGet("/{id:guid}", async (Guid id, TicketsService s, CancellationToken ct) => (await s.GetAsync(id, ct)).ToHttpResult())
            .RequirePermission(Permissions.Support.TicketView).Produces<TicketDetailDto>().WithSummary("One ticket with the whole thread (internal notes included) and its status history.");

        g.MapPost("", async (StaffCreateTicketRequest r, TicketsService s, CancellationToken ct) => (await s.CreateAsync(r, ct)).ToHttpResult(v => Results.Created($"/api/v1/support/tickets/{v.Id}", v)))
            .RequirePermission(Permissions.Support.TicketCreate).Produces<TicketDetailDto>(StatusCodes.Status201Created)
            .WithSummary("Open a ticket on behalf of a customer (phone, WhatsApp, Facebook, e-mail). An order number ties it to that order's branch; without one it is global.");

        g.MapPost("/uploads", async (IFormFile file, TicketsService s, CancellationToken ct) =>
            {
                await using var stream = file.OpenReadStream();
                return (await s.UploadAsync(stream, file.FileName, ct)).ToHttpResult(v => Results.Created(v.Url, v));
            })
            .DisableAntiforgery().WithMetadata(new RequestSizeLimitAttribute(10 * 1024 * 1024)).RequirePermission(Permissions.Support.TicketReply).Produces<TicketUploadDto>(StatusCodes.Status201Created)
            .WithSummary("Upload a picture or PDF (JPEG, PNG, WebP or PDF, up to 8 MB). Send the returned path in attachmentPaths when creating a ticket or replying.");

        g.MapPost("/{id:guid}/reply", async (Guid id, StaffReplyRequest r, TicketsService s, CancellationToken ct) => (await s.ReplyAsync(id, r, ct)).ToHttpResult())
            .RequirePermission(Permissions.Support.TicketReply).Produces<TicketDetailDto>()
            .WithSummary("Answer the customer (internal=false; tells them) or write a note for staff only (internal=true). A first reply moves an Open ticket to InProgress; setStatus changes the status after the message.");

        g.MapPost("/{id:guid}/assign", async (Guid id, AssignTicketRequest r, TicketsService s, CancellationToken ct) => (await s.AssignAsync(id, r, ct)).ToHttpResult())
            .RequirePermission(Permissions.Support.TicketAssign).Produces<TicketDetailDto>().WithSummary("Assign the ticket to a staff member who may work on its branch, or send assigneeId=null to unassign.");

        g.MapPut("/{id:guid}/priority", async (Guid id, ChangePriorityRequest r, TicketsService s, CancellationToken ct) => (await s.PriorityAsync(id, r, ct)).ToHttpResult())
            .RequirePermission(Permissions.Support.TicketAssign).Produces<TicketDetailDto>().WithSummary("Change the priority; the due times are recalculated from the creation time.");

        g.MapPost("/{id:guid}/status", async (Guid id, ChangeStatusRequest r, TicketsService s, CancellationToken ct) => (await s.ChangeStatusAsync(id, r, ct)).ToHttpResult())
            .RequirePermission(Permissions.Support.TicketReply).Produces<TicketDetailDto>()
            .WithSummary("Move the ticket to another status. Closing and reopening also need the close permission; a reopening after the reopen window answers TICKET.REOPEN_WINDOW_EXPIRED.");

        g.MapPost("/{id:guid}/resolve", async (Guid id, ReasonRequest r, TicketsService s, CancellationToken ct) => (await s.ResolveAsync(id, r, ct)).ToHttpResult())
            .RequirePermission(Permissions.Support.TicketReply).Produces<TicketDetailDto>().WithSummary("Mark the ticket Resolved (the customer is told).");

        g.MapPost("/{id:guid}/close", async (Guid id, ReasonRequest r, TicketsService s, CancellationToken ct) => (await s.CloseAsync(id, r, ct)).ToHttpResult())
            .RequirePermission(Permissions.Support.TicketClose).Produces<TicketDetailDto>().WithSummary("Close the ticket.");

        g.MapPost("/{id:guid}/reopen", async (Guid id, ReasonRequest r, TicketsService s, CancellationToken ct) => (await s.ReopenAsync(id, r, ct)).ToHttpResult())
            .RequirePermission(Permissions.Support.TicketClose).Produces<TicketDetailDto>().WithSummary("Reopen a Resolved or Closed ticket inside the reopen window (setting Support.ReopenDays).");

        g.MapPost("/bulk", async (BulkTicketsRequest r, TicketsService s, CancellationToken ct) => (await s.BulkAsync(r, ct)).ToHttpResult())
            .RequirePermission(Permissions.Support.TicketView).Produces<BulkResultDto>()
            .WithSummary("Assign or close up to 50 tickets at once. Every ticket is checked on its own (permission, branch, state); the answer says which succeeded. More than 50 answers TICKET.BULK_LIMIT.");
    }

    private static void MapCannedReplies(RouteGroupBuilder g)
    {
        g.MapGet("", async (string? language, string? category, bool? includeInactive, CannedRepliesService s, CancellationToken ct) =>
                (await s.ListAsync(new CannedReplyQuery(language, category, includeInactive == true), ct)).ToHttpResult())
            .RequirePermission(Permissions.Support.CannedReplyView).Produces<IReadOnlyList<CannedReplyDto>>().WithSummary("Canned replies, by language (en, bn) and title.");

        g.MapPost("", async (SaveCannedReplyRequest r, CannedRepliesService s, CancellationToken ct) => (await s.CreateAsync(r, ct)).ToHttpResult(v => Results.Created($"/api/v1/support/canned-replies/{v.Id}", v)))
            .RequirePermission(Permissions.Support.CannedReplyEdit).Produces<CannedReplyDto>(StatusCodes.Status201Created)
            .WithSummary("Add a canned reply. Placeholders: {customerName}, {orderNumber}, {ticketNumber}; any other {word} is refused.");

        g.MapPut("/{id:guid}", async (Guid id, SaveCannedReplyRequest r, CannedRepliesService s, CancellationToken ct) => (await s.UpdateAsync(id, r, ct)).ToHttpResult())
            .RequirePermission(Permissions.Support.CannedReplyEdit).Produces<CannedReplyDto>().WithSummary("Change a canned reply or switch it off with isActive=false.");

        g.MapDelete("/{id:guid}", async (Guid id, CannedRepliesService s, CancellationToken ct) => (await s.DeleteAsync(id, ct)).ToHttpResult())
            .RequirePermission(Permissions.Support.CannedReplyEdit).Produces(StatusCodes.Status204NoContent).WithSummary("Remove a canned reply.");

        g.MapPost("/render", async (RenderCannedReplyRequest r, CannedRepliesService s, CancellationToken ct) => (await s.RenderAsync(r, ct)).ToHttpResult())
            .RequirePermission(Permissions.Support.CannedReplyView).Produces<RenderedReplyDto>()
            .WithSummary("Preview a canned reply (cannedReplyId) or any text (body) with the placeholders filled from a ticket you may read (ticketId).");
    }

    private static void MapMine(RouteGroupBuilder g)
    {
        g.MapGet("", async ([AsParameters] ListParams list, TicketStatus? status, MyTicketsService s, CancellationToken ct) => (await s.ListAsync(list.ToRequest(), status, ct)).ToHttpResult())
            .Produces<PagedResult<MyTicketListItemDto>>().WithSummary("My tickets, most recent activity first. Filter by status; search number, subject, order number.");

        g.MapPost("", async (CreateMyTicketRequest r, MyTicketsService s, CancellationToken ct) => (await s.CreateAsync(r, ct)).ToHttpResult(v => Results.Created($"/api/v1/me/tickets/{v.Id}", v)))
            .Produces<MyTicketDto>(StatusCodes.Status201Created).WithSummary("Write to support. An order number must be one of my own orders (TICKET.ORDER_MISMATCH otherwise). Limited per mobile number per hour.");

        g.MapGet("/{id:guid}", async (Guid id, MyTicketsService s, CancellationToken ct) => (await s.GetAsync(id, ct)).ToHttpResult())
            .Produces<MyTicketDto>().WithSummary("One of my tickets with the conversation. Internal notes are never included; staff appear as 'Juvo BD Support'.");

        g.MapPost("/uploads", async (IFormFile file, MyTicketsService s, CancellationToken ct) =>
            {
                await using var stream = file.OpenReadStream();
                return (await s.UploadAsync(stream, file.FileName, ct)).ToHttpResult(v => Results.Created(v.Url, v));
            })
            .DisableAntiforgery().WithMetadata(new RequestSizeLimitAttribute(10 * 1024 * 1024)).Produces<TicketUploadDto>(StatusCodes.Status201Created)
            .WithSummary("Upload a picture or PDF (JPEG, PNG, WebP or PDF, up to 8 MB). Send the returned path in attachmentPaths.");

        g.MapPost("/{id:guid}/reply", async (Guid id, CustomerReplyRequest r, MyTicketsService s, CancellationToken ct) => (await s.ReplyAsync(id, r, ct)).ToHttpResult())
            .Produces<MyTicketDto>().WithSummary("Add a message. It moves a Waiting ticket back to InProgress and reopens a Resolved one (inside the reopen window). A closed ticket answers TICKET.CLOSED.");

        g.MapPost("/{id:guid}/close", async (Guid id, MyTicketsService s, CancellationToken ct) => (await s.CloseAsync(id, ct)).ToHttpResult())
            .Produces<MyTicketDto>().WithSummary("Close my ticket.");

        g.MapPost("/{id:guid}/reopen", async (Guid id, MyTicketsService s, CancellationToken ct) => (await s.ReopenAsync(id, ct)).ToHttpResult())
            .Produces<MyTicketDto>().WithSummary("Reopen a Resolved or Closed ticket inside the reopen window (TICKET.REOPEN_WINDOW_EXPIRED after it).");
    }
}
