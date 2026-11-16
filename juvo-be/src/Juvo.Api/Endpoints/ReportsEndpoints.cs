using Juvo.Api.Configuration;
using Juvo.Api.Http;
using Juvo.Application.Reports;
using Juvo.Shared.Security;

namespace Juvo.Api.Endpoints;

/// <summary>
/// Reports (module P), the profit and loss and the accountant export (module X). Every endpoint names its permission; the services prove it again per branch, so a branch-limited person
/// only ever gets the branches where they hold it. All reads aggregate in SQL; dates are Bangladesh days.
/// </summary>
internal static class ReportsEndpoints
{
    public static WebApplication MapReportsEndpoints(this WebApplication app)
    {
        var v1 = app.MapApiV1();
        var g = v1.MapGroup("/reports").WithTags("Reports");

        g.MapGet("/dashboard", async (Guid? branchId, DashboardService s, CancellationToken ct) => (await s.GetAsync(branchId, ct)).ToHttpResult())
            .RequirePermission(Permissions.Reports.DashboardView).Produces<DashboardDto>()
            .WithSummary("Admin dashboard: today and a 14 day series of orders and sales, orders by status, pending actions, top products and COD pending. branchId limits it to one branch.");

        g.MapGet("/sales", async (DateOnly? from, DateOnly? to, Guid? branchId, string? groupBy, int? limit, SalesReportService s, CancellationToken ct) =>
                (await s.GetAsync(new ReportQuery(from, to, branchId, groupBy, limit), Permissions.Reports.SalesView, ct)).ToHttpResult())
            .RequirePermission(Permissions.Reports.SalesView).Produces<SalesReportDto>()
            .WithSummary("Sales (FR-P01). groupBy: day (default), week, month, product, category, size, color, channel, zone or payment. Orders, units, gross, discount, net, delivery, total.");

        g.MapGet("/orders", async (DateOnly? from, DateOnly? to, Guid? branchId, int? limit, OrdersReportService s, CancellationToken ct) =>
                (await s.GetAsync(new ReportQuery(from, to, branchId, null, limit), Permissions.Reports.OrdersView, ct)).ToHttpResult())
            .RequirePermission(Permissions.Reports.OrdersView).Produces<OrdersReportDto>()
            .WithSummary("Order funnel (FR-P02): placed, confirmed, packed, shipped, delivered, cancelled, returned with counts and rates, plus cancellation and return reasons.");

        g.MapGet("/inventory", async (DateOnly? from, DateOnly? to, Guid? branchId, int? days, int? limit, InventoryReportService s, CancellationToken ct) =>
                (await s.GetAsync(new ReportQuery(from, to, branchId, null, limit, days), Permissions.Reports.InventoryView, ct)).ToHttpResult())
            .RequirePermission(Permissions.Reports.InventoryView).Produces<InventoryReportDto>()
            .WithSummary("Inventory (FR-P03): stock value at cost (with cost permission) and at price, low stock, movements of the period, dead stock (no sale for days, default 60).");

        g.MapGet("/finance", async (DateOnly? from, DateOnly? to, Guid? branchId, string? groupBy, FinanceReportService s, CancellationToken ct) =>
                (await s.GetAsync(new ReportQuery(from, to, branchId, groupBy), Permissions.Reports.FinanceView, ct)).ToHttpResult())
            .RequirePermission(Permissions.Reports.FinanceView).Produces<FinanceReportDto>()
            .WithSummary("Finance (FR-P04): revenue, discounts, delivery collected versus paid to couriers, gateway fees (not recorded), refunds, COD pending versus settled, gross profit (cost permission).");

        g.MapGet("/courier", async (DateOnly? from, DateOnly? to, Guid? branchId, CourierReportService s, CancellationToken ct) =>
                (await s.GetAsync(new ReportQuery(from, to, branchId), Permissions.Reports.CourierView, ct)).ToHttpResult())
            .RequirePermission(Permissions.Reports.CourierView).Produces<CourierReportDto>()
            .WithSummary("Courier performance and COD settlement (FR-P05): delivered, failed and returned rates, average delivery time, cost per parcel, cash collected versus settled.");

        g.MapGet("/customers", async (DateOnly? from, DateOnly? to, Guid? branchId, int? limit, CustomersReportService s, CancellationToken ct) =>
                (await s.GetAsync(new ReportQuery(from, to, branchId, null, limit), Permissions.Reports.CustomersView, ct)).ToHttpResult())
            .RequirePermission(Permissions.Reports.CustomersView).Produces<CustomersReportDto>()
            .WithSummary("Customers (FR-P06): new versus returning, top spenders, guest to profile conversion.");

        MapExport(g, "sales", Permissions.Reports.SalesExport);
        MapExport(g, "orders", Permissions.Reports.OrdersExport);
        MapExport(g, "inventory", Permissions.Reports.InventoryExport);
        MapExport(g, "finance", Permissions.Reports.FinanceExport);
        MapExport(g, "courier", Permissions.Reports.CourierExport);
        MapExport(g, "customers", Permissions.Reports.CustomersExport);

        var a = v1.MapGroup("/accounting").WithTags("Accounting reports");
        a.MapGet("/pnl", async (DateOnly? from, DateOnly? to, Guid? branchId, string? groupBy, PnlService s, CancellationToken ct) =>
                (await s.GetAsync(new ReportQuery(from, to, branchId, groupBy), ct)).ToHttpResult())
            .RequirePermission(Permissions.Accounting.PnlView).Produces<PnlDto>()
            .WithSummary("Profit and loss (FR-X04): period total and series by day, week or month, expenses by category. Cost of goods, gross and net profit are null without the cost permission.");

        a.MapGet("/export", async (DateOnly? from, DateOnly? to, Guid? branchId, string? kind, string? format, string? groupBy, AccountantExportService s, CancellationToken ct) =>
                (await s.ExportAsync(new ReportQuery(from, to, branchId, groupBy), kind, format, ct)).ToHttpResult(f => Results.File(f.Content, f.ContentType, f.FileName)))
            .RequirePermission(Permissions.Accounting.ExportRun).Produces(StatusCodes.Status200OK, contentType: "text/csv")
            .WithSummary("Accountant export (FR-X05): kind daybook, expenses, pnl, payables or purchases; format csv (UTF-8 with BOM) or xlsx.");
        return app;
    }

    private static void MapExport(RouteGroupBuilder g, string report, string permission) =>
        g.MapGet($"/{report}/export", async (DateOnly? from, DateOnly? to, Guid? branchId, string? groupBy, int? limit, int? days, string? format, string? lang, string? section, ReportExportService s, CancellationToken ct) =>
                (await s.ExportAsync(report, new ReportQuery(from, to, branchId, groupBy, limit, days), format, lang, section, ct)).ToHttpResult(f => Results.File(f.Content, f.ContentType, f.FileName)))
            .RequirePermission(permission).Produces(StatusCodes.Status200OK, contentType: "text/csv")
            .WithSummary($"Export the {report} report as csv, xlsx or pdf (lang en or bn). Same filters as the report; CSV and Excel hold one table (section picks it), the PDF holds all.");
}
