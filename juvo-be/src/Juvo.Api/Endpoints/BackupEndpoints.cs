using Juvo.Api.Configuration;
using Juvo.Api.Http;
using Juvo.Application.Backup;
using Juvo.Shared.Pagination;
using Juvo.Shared.Security;

namespace Juvo.Api.Endpoints;

/// <summary>
/// Database backups for staff (FR-AF03): list, start now, one run, status, and the data inventory. Platform-wide and Super Admin by default. There is no restore endpoint and no way
/// to download a backup file: restore is an operator task (<c>scripts/restore-db.ps1</c>), see docs/backup-api.md.
/// </summary>
internal static class BackupEndpoints
{
    public static WebApplication MapBackupEndpoints(this WebApplication app)
    {
        var system = app.MapApiV1().MapGroup("/system");
        var backups = system.MapGroup("/backups").WithTags("System backups");

        backups.MapGet("", async ([AsParameters] ListParams list, BackupService s, CancellationToken ct) => (await s.ListAsync(list.ToRequest(), ct)).ToHttpResult())
            .RequirePermission(Permissions.SystemOps.BackupView).Produces<PagedResult<BackupRunDto>>()
            .WithSummary("Backup runs, newest first: trigger, status, file name, size, verified flag, scrubbed error.");

        backups.MapPost("", async (BackupService s, CancellationToken ct) =>
            {
                var result = await s.StartAsync(ct);
                return result.IsSuccess ? Results.Accepted($"/api/v1/system/backups/{result.Value.Id}", result.Value) : result.ToHttpResult();
            })
            .RequirePermission(Permissions.SystemOps.BackupRun).Produces<BackupRunDto>(StatusCodes.Status202Accepted)
            .WithSummary("Start a backup now. Answers 202 with the run; the work continues in the background (poll the run). 409 SYSTEM.BACKUP_RUNNING when one is already running. Audited.");

        backups.MapGet("/status", async (BackupService s, CancellationToken ct) => (await s.StatusAsync(ct)).ToHttpResult())
            .RequirePermission(Permissions.SystemOps.BackupView).Produces<BackupStatusDto>()
            .WithSummary("Last run, age of the last verified backup, folder, folder free space, retention, nightly time and the next scheduled time.");

        backups.MapGet("/{id:guid}", async (Guid id, BackupService s, CancellationToken ct) => (await s.GetAsync(id, ct)).ToHttpResult())
            .RequirePermission(Permissions.SystemOps.BackupView).Produces<BackupRunDto>().WithSummary("One backup run. 404 SYSTEM.BACKUP_NOT_FOUND.");

        system.MapGet("/data-inventory", async (DataInventoryService s, CancellationToken ct) => (await s.GetAsync(ct)).ToHttpResult())
            .WithTags("System health").RequirePermission(Permissions.SystemOps.HealthView).Produces<DataInventoryDto>()
            .WithSummary("Row counts of the main tables (customers, orders, products, variants, stock levels, invoices, payments, audit rows) and the size of the uploaded files folder. No personal data.");

        return app;
    }
}
