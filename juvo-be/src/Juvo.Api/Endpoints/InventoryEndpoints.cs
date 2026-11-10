using Juvo.Api.Configuration;
using Juvo.Api.Http;
using Juvo.Application.Inventory;
using Juvo.Domain.Inventory;
using Juvo.Shared.Pagination;
using Juvo.Shared.Security;

namespace Juvo.Api.Endpoints;

/// <summary>
/// Branch-wise inventory (module V): stock levels, adjustments, thresholds, movements, inter-branch transfers and stock counts.
/// Endpoints prove the permission somewhere; the services prove it in the branch the action targets.
/// </summary>
internal static class InventoryEndpoints
{
    public static WebApplication MapInventoryEndpoints(this WebApplication app)
    {
        var inventory = app.MapApiV1().MapGroup("/inventory").WithTags("Inventory");

        MapStock(inventory);
        MapTransfers(inventory.MapGroup("/transfers"));
        MapCounts(inventory.MapGroup("/counts"));
        return app;
    }

    private static void MapStock(RouteGroupBuilder g)
    {
        g.MapGet("/stock", async ([AsParameters] ListParams list, Guid? branchId, Guid? productId, bool? lowOnly, StockService s, CancellationToken ct) =>
                (await s.ListAsync(list.ToRequest(), new StockListQuery(branchId, productId, lowOnly), ct)).ToHttpResult())
            .RequirePermission(Permissions.Inventory.StockView).Produces<PagedResult<StockRowDto>>()
            .WithSummary("Stock per variant and branch: on hand, reserved, available, threshold. Only branches where you may view stock.");

        g.MapGet("/stock/products/{productId:guid}", async (Guid productId, StockService s, CancellationToken ct) => (await s.ForProductAsync(productId, ct)).ToHttpResult())
            .RequirePermission(Permissions.Inventory.StockView).Produces<ProductStockDto>()
            .WithSummary("Every variant of a product against every branch you may view (zero where never stocked).");

        g.MapPost("/adjustments", async (AdjustStockRequest r, StockService s, CancellationToken ct) => (await s.AdjustAsync(r, ct)).ToHttpResult())
            .RequirePermission(Permissions.Inventory.StockAdjust).Produces<IReadOnlyList<StockRowDto>>()
            .WithSummary("Add or remove stock with a reason (opening stock, correction, damage, loss, found, customer return). All lines or none.");

        g.MapPut("/reorder-levels", async (SetReorderLevelRequest r, StockService s, CancellationToken ct) => (await s.SetReorderLevelAsync(r, ct)).ToHttpResult())
            .RequirePermission(Permissions.Inventory.ReorderEdit).Produces<StockRowDto>()
            .WithSummary("Set the low-stock threshold of a variant in a branch.");

        g.MapGet("/movements", async ([AsParameters] ListParams list, Guid? branchId, Guid? variantId, Guid? productId, StockReason? reason,
                DateTimeOffset? from, DateTimeOffset? to, StockService s, CancellationToken ct) =>
                (await s.MovementsAsync(list.ToRequest(), new MovementListQuery(branchId, variantId, productId, reason, from, to), ct)).ToHttpResult())
            .RequirePermission(Permissions.Inventory.StockView).Produces<PagedResult<StockMovementDto>>()
            .WithSummary("The append-only stock ledger, newest first.");
    }

    private static void MapTransfers(RouteGroupBuilder g)
    {
        g.MapGet("", async ([AsParameters] ListParams list, TransferStatus? status, Guid? branchId, TransfersService s, CancellationToken ct) =>
                (await s.ListAsync(list.ToRequest(), new TransferListQuery(status, branchId), ct)).ToHttpResult())
            .RequirePermission(Permissions.Inventory.TransferView).Produces<PagedResult<TransferDto>>().WithSummary("Transfers where you may view either branch.");

        g.MapGet("/{id:guid}", async (Guid id, TransfersService s, CancellationToken ct) => (await s.GetAsync(id, ct)).ToHttpResult())
            .RequirePermission(Permissions.Inventory.TransferView).Produces<TransferDto>().WithSummary("One transfer with its lines and the actions you may take.");

        g.MapPost("", async (RequestTransferRequest r, TransfersService s, CancellationToken ct) =>
                (await s.RequestAsync(r, ct)).ToHttpResult(v => Results.Created($"/api/v1/inventory/transfers/{v.Id}", v)))
            .RequirePermission(Permissions.Inventory.TransferRequest).Produces<TransferDto>(StatusCodes.Status201Created)
            .WithSummary("Request a transfer (either the sending or the receiving branch may ask).");

        g.MapPost("/{id:guid}/approve", async (Guid id, TransfersService s, CancellationToken ct) => (await s.ApproveAsync(id, ct)).ToHttpResult())
            .RequirePermission(Permissions.Inventory.TransferApprove).Produces<TransferDto>().WithSummary("Approve (source branch). Checks the stock is there.");

        g.MapPost("/{id:guid}/reject", async (Guid id, ReasonRequest r, TransfersService s, CancellationToken ct) => (await s.RejectAsync(id, r, ct)).ToHttpResult())
            .RequirePermission(Permissions.Inventory.TransferApprove).Produces<TransferDto>().WithSummary("Reject a requested transfer (source branch).");

        g.MapPost("/{id:guid}/ship", async (Guid id, TransfersService s, CancellationToken ct) => (await s.ShipAsync(id, ct)).ToHttpResult())
            .RequirePermission(Permissions.Inventory.TransferApprove).Produces<TransferDto>().WithSummary("Ship (source branch): stock leaves the source now.");

        g.MapPost("/{id:guid}/receive", async (Guid id, TransfersService s, CancellationToken ct) => (await s.ReceiveAsync(id, ct)).ToHttpResult())
            .RequirePermission(Permissions.Inventory.TransferReceive).Produces<TransferDto>().WithSummary("Receive (destination branch): stock enters the destination.");

        g.MapPost("/{id:guid}/cancel", async (Guid id, ReasonRequest r, TransfersService s, CancellationToken ct) => (await s.CancelAsync(id, r, ct)).ToHttpResult())
            .RequirePermission(Permissions.Inventory.TransferCancel).Produces<TransferDto>().WithSummary("Cancel before receipt. Goods in transit go back to the source.");
    }

    private static void MapCounts(RouteGroupBuilder g)
    {
        g.MapGet("", async ([AsParameters] ListParams list, CountStatus? status, Guid? branchId, CountsService s, CancellationToken ct) =>
                (await s.ListAsync(list.ToRequest(), new CountListQuery(status, branchId), ct)).ToHttpResult())
            .RequirePermission(Permissions.Inventory.CountView).Produces<PagedResult<CountListItemDto>>().WithSummary("Stock counts of the branches you may view.");

        g.MapGet("/{id:guid}", async (Guid id, CountsService s, CancellationToken ct) => (await s.GetAsync(id, ct)).ToHttpResult())
            .RequirePermission(Permissions.Inventory.CountView).Produces<CountDto>().WithSummary("One stock count with its lines and differences.");

        g.MapPost("", async (StartCountRequest r, CountsService s, CancellationToken ct) =>
                (await s.StartAsync(r, ct)).ToHttpResult(v => Results.Created($"/api/v1/inventory/counts/{v.Summary.Id}", v)))
            .RequirePermission(Permissions.Inventory.CountCreate).Produces<CountDto>(StatusCodes.Status201Created)
            .WithSummary("Start a count of a branch (all stocked variants, or the variants listed). Snapshots the system quantity.");

        g.MapPut("/{id:guid}/lines", async (Guid id, SaveCountLinesRequest r, CountsService s, CancellationToken ct) => (await s.SaveLinesAsync(id, r, ct)).ToHttpResult())
            .RequirePermission(Permissions.Inventory.CountCreate).Produces<CountDto>().WithSummary("Enter counted quantities (null clears a line).");

        g.MapPost("/{id:guid}/submit", async (Guid id, CountsService s, CancellationToken ct) => (await s.SubmitAsync(id, ct)).ToHttpResult())
            .RequirePermission(Permissions.Inventory.CountCreate).Produces<CountDto>().WithSummary("Submit for review.");

        g.MapPost("/{id:guid}/reopen", async (Guid id, CountsService s, CancellationToken ct) => (await s.ReopenAsync(id, ct)).ToHttpResult())
            .RequirePermission(Permissions.Inventory.CountApprove).Produces<CountDto>().WithSummary("Send a submitted count back for more counting.");

        g.MapPost("/{id:guid}/approve", async (Guid id, CountsService s, CancellationToken ct) => (await s.ApproveAsync(id, ct)).ToHttpResult())
            .RequirePermission(Permissions.Inventory.CountApprove).Produces<CountDto>().WithSummary("Approve: the differences are booked as stock movements.");

        g.MapPost("/{id:guid}/cancel", async (Guid id, CountsService s, CancellationToken ct) => (await s.CancelAsync(id, ct)).ToHttpResult())
            .RequirePermission(Permissions.Inventory.CountCreate).Produces<CountDto>().WithSummary("Cancel an open count; nothing is booked.");
    }
}
