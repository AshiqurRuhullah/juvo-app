using Juvo.Api.Configuration;
using Juvo.Api.Http;
using Juvo.Application.Returns;
using Juvo.Domain.Returns;
using Juvo.Shared.Pagination;
using Juvo.Shared.Security;
using Microsoft.AspNetCore.Mvc;

namespace Juvo.Api.Endpoints;

/// <summary>
/// Returns and exchanges (module L). The customer side needs a customer token; every staff endpoint names its permission (the services prove it again in the
/// return's own branch, so another branch's return looks like "not found").
/// </summary>
internal static class ReturnsEndpoints
{
    private const string IdempotencyHeader = "Idempotency-Key";

    public static WebApplication MapReturnsEndpoints(this WebApplication app)
    {
        var v1 = app.MapApiV1();
        MapMe(v1.MapGroup("/me").WithTags("Customer returns").RequireAuthorization(Policies.Customer));
        MapStaff(v1.MapGroup("/returns").WithTags("Returns (staff)"));
        return app;
    }

    private static void MapMe(RouteGroupBuilder g)
    {
        g.MapGet("/orders/{id:guid}/return-eligibility", async (Guid id, CustomerReturnsService s, CancellationToken ct) => (await s.EligibilityAsync(id, ct)).ToHttpResult())
            .Produces<ReturnEligibilityDto>()
            .WithSummary("What of my order can still be returned: the deadline, the quantity left per line (non-returnable items and what is already in a return are excluded), the refund per unit, the list of reasons.");

        g.MapPost("/returns/photos", async (IFormFile file, CustomerReturnsService s, CancellationToken ct) =>
            {
                await using var stream = file.OpenReadStream();
                return (await s.UploadPhotoAsync(stream, ct)).ToHttpResult(v => Results.Created(v.Url, v));
            })
            .DisableAntiforgery().WithMetadata(new RequestSizeLimitAttribute(12 * 1024 * 1024)).Produces<ReturnPhotoUploadDto>(StatusCodes.Status201Created)
            .WithSummary("Upload one picture of the item (JPEG, PNG or WebP, at least 1000 px on the long side, at most 10 MB). Send the returned path in the return request (up to 5).");

        g.MapPost("/returns", async ([FromHeader(Name = IdempotencyHeader)] string? key, CreateReturnRequest r, CustomerReturnsService s, CancellationToken ct) =>
                (await s.CreateAsync(string.IsNullOrWhiteSpace(r.IdempotencyKey) ? r with { IdempotencyKey = key } : r, ct)).ToHttpResult(v => Results.Created($"/api/v1/me/returns/{v.Id}", v)))
            .Produces<MyReturnDto>(StatusCodes.Status201Created)
            .WithSummary("Ask for a return or an exchange of delivered items within the return window: lines with quantities, a reason, optional details and pictures. Send Idempotency-Key to make retries safe.");

        g.MapGet("/returns", async ([AsParameters] ListParams list, string? filter, CustomerReturnsService s, CancellationToken ct) => (await s.ListAsync(list.ToRequest(), filter, ct)).ToHttpResult())
            .Produces<PagedResult<MyReturnListItemDto>>().WithSummary("My returns, newest first. filter: active, closed.");

        g.MapGet("/returns/{id:guid}", async (Guid id, CustomerReturnsService s, CancellationToken ct) => (await s.GetAsync(id, ct)).ToHttpResult())
            .Produces<MyReturnDto>().WithSummary("One of my returns with lines, pictures, the refund amounts, pickup details and a timeline.");

        g.MapPost("/returns/{id:guid}/cancel", async (Guid id, CancelReturnRequest r, CustomerReturnsService s, CancellationToken ct) => (await s.CancelAsync(id, r, ct)).ToHttpResult())
            .Produces<MyReturnDto>().WithSummary("Cancel my return while it is Requested or Approved.");
    }

    private static void MapStaff(RouteGroupBuilder g)
    {
        g.MapGet("", async ([AsParameters] ListParams list, ReturnStatus? status, ReturnKind? kind, ReturnFault? fault, Guid? branchId, DateTimeOffset? from, DateTimeOffset? to, ReturnsService s, CancellationToken ct) =>
                (await s.ListAsync(list.ToRequest(), new ReturnListQuery(status, kind, fault, branchId, from, to), ct)).ToHttpResult())
            .RequirePermission(Permissions.Returns.View).Produces<PagedResult<ReturnListItemDto>>()
            .WithSummary("The return queue of the branches where you may view returns. Filters: status, kind, fault, branch, date range; search by return number, order number, mobile or name; sort requestedAt (default newest), number, status.");

        g.MapGet("/summary", async (ReturnsService s, CancellationToken ct) => (await s.SummaryAsync(ct)).ToHttpResult())
            .RequirePermission(Permissions.Returns.View).Produces<ReturnSummaryDto>().WithSummary("Counts for the dashboard: returns waiting for approval, pickup, inspection, refund or exchange, and the money refunded this month.");

        g.MapGet("/{id:guid}", async (Guid id, ReturnsService s, CancellationToken ct) => (await s.GetAsync(id, ct)).ToHttpResult())
            .RequirePermission(Permissions.Returns.View).Produces<ReturnDetailDto>()
            .WithSummary("One return: lines with the refund of each, pictures, pickup, the refund breakdown, the order's tracks, notes, the timeline and the statuses it may move to.");

        g.MapPost("/{id:guid}/approve", async (Guid id, ApproveReturnRequest r, ReturnsService s, CancellationToken ct) => (await s.ApproveAsync(id, r, ct)).ToHttpResult())
            .RequirePermission(Permissions.Returns.Approve).Produces<ReturnDetailDto>()
            .WithSummary("Approve a requested return. The order moves from Delivered to ReturnInProgress.");

        g.MapPost("/{id:guid}/reject", async (Guid id, RejectReturnRequest r, ReturnsService s, CancellationToken ct) => (await s.RejectAsync(id, r, ct)).ToHttpResult())
            .RequirePermission(Permissions.Returns.Reject).Produces<ReturnDetailDto>().WithSummary("Reject a requested return with a reason the customer will see.");

        g.MapPost("/{id:guid}/schedule-pickup", async (Guid id, SchedulePickupRequest r, ReturnsService s, CancellationToken ct) => (await s.SchedulePickupAsync(id, r, ct)).ToHttpResult())
            .RequirePermission(Permissions.Returns.Manage).Produces<ReturnDetailDto>().WithSummary("Arrange the pickup of an approved return: date, courier, note.");

        g.MapPost("/{id:guid}/picked-up", async (Guid id, ReturnsService s, CancellationToken ct) => (await s.MarkPickedUpAsync(id, ct)).ToHttpResult())
            .RequirePermission(Permissions.Returns.Manage).Produces<ReturnDetailDto>().WithSummary("The courier collected the parcel from the customer.");

        g.MapPost("/{id:guid}/received", async (Guid id, ReturnsService s, CancellationToken ct) => (await s.MarkReceivedAsync(id, ct)).ToHttpResult())
            .RequirePermission(Permissions.Returns.Receive).Produces<ReturnDetailDto>().WithSummary("The parcel arrived at the branch.");

        g.MapPost("/{id:guid}/inspect", async (Guid id, InspectReturnRequest r, ReturnsService s, CancellationToken ct) => (await s.InspectAsync(id, r, ct)).ToHttpResult())
            .RequirePermission(Permissions.Returns.Inspect).Produces<ReturnDetailDto>()
            .WithSummary("Record what was found per line (condition, back on the shelf yes or no, refund accepted) and who is at fault. Good items go back into the branch stock with a stock movement; the refund amount is fixed here.");

        g.MapPost("/{id:guid}/refund", async (Guid id, RefundReturnRequest r, ReturnsService s, CancellationToken ct) => (await s.RefundAsync(id, r, ct)).ToHttpResult())
            .RequirePermission(Permissions.Returns.Refund).Produces<ReturnDetailDto>()
            .WithSummary("Record the refund of an inspected return: method, reference and (optionally) the amount, which must match the server's figure. The order's payment becomes PartiallyRefunded or Refunded; the money itself is moved by staff (the gateway refund comes with Step 9).");

        g.MapPost("/{id:guid}/exchange", async (Guid id, ExchangeReturnRequest r, ReturnsService s, CancellationToken ct) => (await s.ExchangeAsync(id, r, ct)).ToHttpResult())
            .RequirePermission(Permissions.Returns.Exchange).Produces<ReturnDetailDto>()
            .WithSummary("Complete an inspected exchange: a linked zero-value order is placed for the replacement variants and its stock reserved. Refused when a replacement is out of stock.");

        g.MapPost("/{id:guid}/reject-refund", async (Guid id, RejectReturnRequest r, ReturnsService s, CancellationToken ct) => (await s.RejectRefundAsync(id, r, ct)).ToHttpResult())
            .RequirePermission(Permissions.Returns.Reject).Produces<ReturnDetailDto>().WithSummary("After inspection: nothing is refunded or exchanged (for example the item was used or damaged by the customer), with a reason.");

        g.MapPost("/{id:guid}/notes", async (Guid id, ReturnNoteRequest r, ReturnsService s, CancellationToken ct) => (await s.AddNoteAsync(id, r, ct)).ToHttpResult())
            .RequirePermission(Permissions.Returns.Manage).Produces<ReturnDetailDto>().WithSummary("Add an internal note (staff only).");

        g.MapPost("/bulk", async (BulkReturnRequest r, ReturnsService s, CancellationToken ct) => (await s.BulkAsync(r, ct)).ToHttpResult())
            .RequirePermission(Permissions.Returns.Approve).Produces<BulkReturnResult>()
            .WithSummary("Approve or reject up to 100 returns. Each one is handled on its own (permission, branch, state); the answer lists success or the error code per return. Rejecting also needs the reject permission, checked per return.");
    }
}
