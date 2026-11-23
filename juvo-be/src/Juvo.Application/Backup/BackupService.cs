using System.Text.Json;
using Juvo.Application.Abstractions;
using Juvo.Application.Monitoring;
using Juvo.Application.Settings;
using Juvo.Application.Ums;
using Juvo.Domain.Platform;
using Juvo.Shared.Pagination;
using Juvo.Shared.Results;
using Juvo.Shared.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Juvo.Application.Backup;

/// <summary>
/// Staff view and manual start of database backups (FR-AF03). Platform-wide, Super Admin by default. There is no restore here on purpose: restoring replaces the live data and is an
/// operator task done with <c>scripts/restore-db.ps1</c>. No backup file is returned by any call.
/// </summary>
public sealed class BackupService(
    IApplicationDbContext db, ActorContext actors, ICurrentUser currentUser, BackupCoordinator coordinator, BackupRunner runner, IBackupEngine engine, ISettingsReader settings,
    IOptions<BackupOptions> options, TimeProvider clock)
{
    private async Task<(Actor? Actor, Error? Refusal)> RequireAsync(string permission, CancellationToken cancellationToken)
    {
        if (await actors.GetAsync(cancellationToken) is not { } actor)
        {
            return (null, Error.Unauthorized());
        }

        return actor.Access.HasAnywhere(permission) ? (actor, null) : (null, Error.Forbidden());
    }

    internal static BackupRunDto ToDto(BackupRun r) =>
        new(r.Id, r.Trigger, r.TriggeredByUserId, r.Status, r.StartedAt, r.FinishedAt, r.FinishedAt is { } f ? (int)Math.Min(int.MaxValue, (f - r.StartedAt).TotalSeconds) : null, r.FileName, r.SizeBytes,
            r.Compressed, r.Verified, ErrorScrubber.Scrub(r.Error), r.FileDeletedAt);

    public async Task<Result<PagedResult<BackupRunDto>>> ListAsync(PageRequest request, CancellationToken cancellationToken)
    {
        var (_, refusal) = await RequireAsync(Permissions.SystemOps.BackupView, cancellationToken);
        if (refusal is not null)
        {
            return refusal;
        }

        var page = request.Normalized;
        var query = db.BackupRuns.AsNoTracking();
        var total = await query.CountAsync(cancellationToken);
        var rows = await query.OrderByDescending(r => r.StartedAt).ThenByDescending(r => r.Id).Skip(page.Skip).Take(page.PageSize).ToListAsync(cancellationToken);
        return new PagedResult<BackupRunDto>([.. rows.Select(ToDto)], total, page.Page, page.PageSize);
    }

    public async Task<Result<BackupRunDto>> GetAsync(Guid id, CancellationToken cancellationToken)
    {
        var (_, refusal) = await RequireAsync(Permissions.SystemOps.BackupView, cancellationToken);
        if (refusal is not null)
        {
            return refusal;
        }

        var run = await db.BackupRuns.AsNoTracking().FirstOrDefaultAsync(r => r.Id == id, cancellationToken);
        return run is null ? Error.NotFound(ErrorCodes.Platform.BackupNotFound, "Backup run not found.") : ToDto(run);
    }

    /// <summary>Starts a backup now. The run row is created here and the work happens in the background; 409 <c>SYSTEM.BACKUP_RUNNING</c> when one is already running. Audited.</summary>
    public async Task<Result<BackupRunDto>> StartAsync(CancellationToken cancellationToken)
    {
        var (actor, refusal) = await RequireAsync(Permissions.SystemOps.BackupRun, cancellationToken);
        if (refusal is not null)
        {
            return refusal;
        }

        if (!coordinator.TryEnter())
        {
            return Error.Conflict(ErrorCodes.Platform.BackupRunning, "A backup is already running. Wait for it to finish.");
        }

        BackupRun run;
        try
        {
            await runner.RecoverInterruptedAsync(cancellationToken);
            run = await runner.BeginAsync(BackupTrigger.User, actor!.Id, cancellationToken);
            var detail = JsonSerializer.Serialize(new { action = "backup.run", runId = run.Id }, JsonSerializerOptions.Web);
            db.AuditLogs.Add(new AuditLog(currentUser.UserId, currentUser.BranchId, BackupSettingKeys.AuditEntity, run.Id.ToString(), AuditAction.Created, null, detail, currentUser.CorrelationId,
                currentUser.IpAddress, clock.GetUtcNow()));
            await db.SaveChangesAsync(cancellationToken);
        }
        catch
        {
            coordinator.Exit();
            throw;
        }

        coordinator.StartInBackground(run.Id);
        return ToDto(run);
    }

    /// <summary>Age of the last verified backup, folder free space, the next scheduled time and the retention.</summary>
    public async Task<Result<BackupStatusDto>> StatusAsync(CancellationToken cancellationToken)
    {
        var (_, refusal) = await RequireAsync(Permissions.SystemOps.BackupView, cancellationToken);
        if (refusal is not null)
        {
            return refusal;
        }

        var now = clock.GetUtcNow();
        var enabled = await settings.GetBoolAsync(BackupSettingKeys.Enabled, cancellationToken);
        var o = options.Value;
        DateTimeOffset? next = enabled && o.WorkerEnabled && BackupSchedule.TryParseTime(o.DailyAt, out var at) ? BackupSchedule.Next(now, at) : null;

        var last = await db.BackupRuns.AsNoTracking().OrderByDescending(r => r.StartedAt).FirstOrDefaultAsync(cancellationToken);
        var verified = await db.BackupRuns.AsNoTracking().Where(r => r.Verified && r.Status == BackupStatus.Succeeded).OrderByDescending(r => r.FinishedAt)
            .Select(r => new { r.FinishedAt, r.FileName }).FirstOrDefaultAsync(cancellationToken);

        return new BackupStatusDto(
            enabled, engine.Folder, o.RetentionDays, o.DailyAt, "Asia/Dhaka (UTC+06:00)", next, coordinator.IsRunning, last is null ? null : ToDto(last), verified?.FinishedAt,
            verified?.FinishedAt is { } done ? Math.Round((now - done).TotalHours, 1) : null, verified?.FileName, engine.FreeSpaceBytes());
    }
}

/// <summary>Row counts of the main tables, so staff can see what the system holds (FR-AF03). Counts include archived and every branch. No personal data is returned.</summary>
public sealed class DataInventoryService(IApplicationDbContext db, ActorContext actors, IBackupEngine engine, TimeProvider clock)
{
    public async Task<Result<DataInventoryDto>> GetAsync(CancellationToken cancellationToken)
    {
        if (await actors.GetAsync(cancellationToken) is not { } actor)
        {
            return Error.Unauthorized();
        }

        if (!actor.Access.HasAnywhere(Permissions.SystemOps.HealthView))
        {
            return Error.Forbidden();
        }

        var rows = new List<DataInventoryRow>
        {
            new("customers", await db.Customers.IgnoreQueryFilters().LongCountAsync(cancellationToken)),
            new("orders", await db.Orders.IgnoreQueryFilters().LongCountAsync(cancellationToken)),
            new("products", await db.Products.IgnoreQueryFilters().LongCountAsync(cancellationToken)),
            new("variants", await db.ProductVariants.IgnoreQueryFilters().LongCountAsync(cancellationToken)),
            new("stockLevels", await db.StockLevels.IgnoreQueryFilters().LongCountAsync(cancellationToken)),
            new("invoices", await db.Invoices.IgnoreQueryFilters().LongCountAsync(cancellationToken)),
            new("payments", await db.Payments.IgnoreQueryFilters().LongCountAsync(cancellationToken)),
            new("auditLogs", await db.AuditLogs.LongCountAsync(cancellationToken)),
        };

        var uploads = engine.MeasureUploads();
        return new DataInventoryDto(clock.GetUtcNow(), rows, uploads.Bytes, uploads.Files, uploads.Truncated);
    }
}
