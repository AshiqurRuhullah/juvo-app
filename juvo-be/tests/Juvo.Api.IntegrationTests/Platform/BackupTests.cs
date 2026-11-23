using System.Net;
using System.Text.Json;
using Juvo.Api.IntegrationTests.Orders;
using Juvo.Api.IntegrationTests.Storefront;
using Juvo.Api.IntegrationTests.Support;
using Juvo.Application.Backup;
using Juvo.Domain.Orders;
using Juvo.Domain.Platform;
using Juvo.Infrastructure.Backup;
using Juvo.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Juvo.Api.IntegrationTests.Platform;

/// <summary>Lets a test hold a backup in the middle of its run (to prove the 409) by blocking the engine until released.</summary>
public sealed class BackupGate
{
    private volatile TaskCompletionSource? _closed;

    public void Close() => _closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

    public void Open() => _closed?.TrySetResult();

    public Task WaitAsync() => _closed?.Task ?? Task.CompletedTask;
}

internal sealed class GatedBackupEngine(IBackupEngine inner, BackupGate gate) : IBackupEngine
{
    public string Folder => inner.Folder;

    public Task<string> GetDatabaseNameAsync(CancellationToken cancellationToken) => inner.GetDatabaseNameAsync(cancellationToken);

    public async Task<BackupEngineResult> BackupAsync(string fileName, CancellationToken cancellationToken)
    {
        await gate.WaitAsync().WaitAsync(cancellationToken);
        return await inner.BackupAsync(fileName, cancellationToken);
    }

    public IReadOnlyList<BackupFileInfo> ListFiles() => inner.ListFiles();

    public bool DeleteFile(string fileName) => inner.DeleteFile(fileName);

    public long? FreeSpaceBytes() => inner.FreeSpaceBytes();

    public UploadsUsage MeasureUploads() => inner.MeasureUploads();
}

/// <summary>A shop whose backups go to a throw-away temp folder; the nightly worker is off and the real SQL Server engine is wrapped in a gate.</summary>
public sealed class BackupFactory : ApiFactory
{
    public string BackupFolder { get; } = Path.Combine(Path.GetTempPath(), "juvo-backup-tests-" + Guid.NewGuid().ToString("N"));

    public BackupGate Gate { get; } = new();

    protected override void ConfigureSettings()
    {
        Setting("Backup:Folder", BackupFolder);
        Setting("Backup:WorkerEnabled", "false");
        Setting("Backup:RetentionDays", "14");
    }

    protected override void ConfigureTestServices(IServiceCollection services)
    {
        services.AddSingleton(Gate);
        services.AddScoped<IBackupEngine>(sp => new GatedBackupEngine(ActivatorUtilities.CreateInstance<SqlServerBackupEngine>(sp), sp.GetRequiredService<BackupGate>()));
    }

    public override async ValueTask DisposeAsync()
    {
        Gate.Open();
        await base.DisposeAsync();
        try
        {
            if (Directory.Exists(BackupFolder))
            {
                Directory.Delete(BackupFolder, true);
            }
        }
        catch (IOException)
        {
            // Best effort: a temp folder left behind holds only test backups.
        }
    }
}

/// <summary>Real backups of the throw-away test database over the real API: file written and verified, listing, status, the single-run rule, permissions, retention and the nightly worker (FR-AF03).</summary>
public class BackupTests(BackupFactory factory) : IClassFixture<BackupFactory>
{
    private const string Base = "/api/v1/system/backups";

    private readonly StoreHelper _h = new(factory);

    private OrderHelper O => new(_h);

    private async Task<T> WithDbAsync<T>(Func<JuvoDbContext, Task<T>> action)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        return await action(scope.ServiceProvider.GetRequiredService<JuvoDbContext>());
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private async Task<JsonElement> WaitDoneAsync(string admin, Guid id)
    {
        var deadline = DateTime.UtcNow.AddMinutes(3);
        while (true)
        {
            var run = await _h.JsonAsync(HttpMethod.Get, $"{Base}/{id}", HttpStatusCode.OK, bearer: admin);
            if (run.GetProperty("status").GetString() != "Running" || DateTime.UtcNow > deadline)
            {
                await (factory.Services.GetRequiredService<BackupCoordinator>().Current ?? Task.CompletedTask); // the slot frees just after the row is final
                return run;
            }

            await Task.Delay(200, Ct);
        }
    }

    private async Task<Guid> StartAsync(string admin)
    {
        var response = await _h.SendAsync(HttpMethod.Post, Base, new { }, admin);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.NotNull(response.Headers.Location);
        return (await TestApi.JsonAsync(response)).GetProperty("id").GetGuid();
    }

    [Fact]
    public async Task A_manual_backup_is_written_verified_listed_audited_and_shown_in_the_status()
    {
        var admin = await _h.AdminAsync();
        factory.Gate.Open();

        var id = await StartAsync(admin);
        var run = await WaitDoneAsync(admin, id);

        Assert.Equal("Succeeded", run.GetProperty("status").GetString());
        Assert.True(run.GetProperty("verified").GetBoolean(), run.ToString());
        Assert.Equal("User", run.GetProperty("trigger").GetString());
        Assert.True(run.GetProperty("sizeBytes").GetInt64() > 0);
        var name = run.GetProperty("fileName").GetString()!;
        Assert.True(BackupNaming.TryParse(name, out _, out _), name);
        Assert.DoesNotContain('@', name);
        Assert.True(File.Exists(Path.Combine(factory.BackupFolder, name)));

        var list = await _h.JsonAsync(HttpMethod.Get, Base, HttpStatusCode.OK, bearer: admin);
        Assert.Contains(list.GetProperty("items").EnumerateArray(), r => r.GetProperty("id").GetGuid() == id);

        var status = await _h.JsonAsync(HttpMethod.Get, $"{Base}/status", HttpStatusCode.OK, bearer: admin);
        Assert.False(status.GetProperty("enabled").GetBoolean());
        Assert.False(status.GetProperty("running").GetBoolean());
        Assert.Equal(14, status.GetProperty("retentionDays").GetInt32());
        Assert.Equal(JsonValueKind.Null, status.GetProperty("nextScheduledAt").ValueKind); // the nightly backup is off by default
        Assert.Equal(name, status.GetProperty("lastVerifiedFileName").GetString());
        Assert.True(status.GetProperty("lastVerifiedAgeHours").GetDouble() < 1);
        Assert.Equal(Path.GetFullPath(factory.BackupFolder), status.GetProperty("folder").GetString());

        var audit = await WithDbAsync(db => db.AuditLogs.AsNoTracking().Where(a => a.Entity == "BackupRun" && a.EntityId == id.ToString()).ToListAsync(Ct));
        Assert.Single(audit);

        Assert.Equal(HttpStatusCode.NotFound, (await _h.SendAsync(HttpMethod.Get, $"{Base}/{Guid.NewGuid()}", null, admin)).StatusCode);
    }

    [Fact]
    public async Task A_second_backup_while_one_is_running_is_refused_with_409_and_the_slot_frees_afterwards()
    {
        var admin = await _h.AdminAsync();
        factory.Gate.Open();
        factory.Gate.Close();
        Guid first;
        try
        {
            first = await StartAsync(admin);

            var second = await _h.SendAsync(HttpMethod.Post, Base, new { }, admin);
            Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
            Assert.Equal("SYSTEM.BACKUP_RUNNING", await TestApi.ErrorCodeAsync(second));

            var status = await _h.JsonAsync(HttpMethod.Get, $"{Base}/status", HttpStatusCode.OK, bearer: admin);
            Assert.True(status.GetProperty("running").GetBoolean());
            Assert.Equal("Running", (await _h.JsonAsync(HttpMethod.Get, $"{Base}/{first}", HttpStatusCode.OK, bearer: admin)).GetProperty("status").GetString());
        }
        finally
        {
            factory.Gate.Open();
        }

        Assert.Equal("Succeeded", (await WaitDoneAsync(admin, first)).GetProperty("status").GetString());
        var again = await StartAsync(admin);
        Assert.Equal("Succeeded", (await WaitDoneAsync(admin, again)).GetProperty("status").GetString());
    }

    [Fact]
    public async Task A_run_left_running_by_a_stopped_process_is_closed_as_failed_when_the_next_backup_starts()
    {
        var admin = await _h.AdminAsync();
        factory.Gate.Open();
        var staleId = await WithDbAsync(async db =>
        {
            var stale = new BackupRun(BackupTrigger.User, null, DateTimeOffset.UtcNow.AddHours(-3));
            db.BackupRuns.Add(stale);
            await db.SaveChangesAsync(Ct);
            return stale.Id;
        });

        var id = await StartAsync(admin);
        await WaitDoneAsync(admin, id);

        var stale2 = await _h.JsonAsync(HttpMethod.Get, $"{Base}/{staleId}", HttpStatusCode.OK, bearer: admin);
        Assert.Equal("Failed", stale2.GetProperty("status").GetString());
        Assert.Contains("stopped", stale2.GetProperty("error").GetString());
    }

    [Fact]
    public async Task Only_staff_with_the_backup_permissions_can_see_or_start_backups_and_no_default_role_but_super_admin_has_them()
    {
        var admin = await _h.AdminAsync();
        var nobody = await O.StaffTokenAsync(admin, null, "orders.order.view");
        var viewer = await O.StaffTokenAsync(admin, null, "system.backup.view");
        var runner = await O.StaffTokenAsync(admin, null, "system.backup.run");
        var healthOnly = await O.StaffTokenAsync(admin, null, "system.health.view");
        var managerRole = await _h.Api.RoleIdAsync("Manager");
        var ho = await _h.Api.BranchIdAsync("HO");
        var manager = await _h.Api.CreateUserAsync(admin, $"{StoreHelper.Unique("mg")}@example.com", "Manager Person", false, (ho, managerRole));
        var managerToken = (await _h.Api.LoginAsync(manager.Email, manager.Password)).Access;
        var id = Guid.NewGuid();
        var reads = new[] { Base, $"{Base}/status", $"{Base}/{id}" };

        foreach (var url in reads)
        {
            Assert.Equal(HttpStatusCode.Unauthorized, (await _h.SendAsync(HttpMethod.Get, url, null, null)).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await _h.SendAsync(HttpMethod.Get, url, null, nobody)).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await _h.SendAsync(HttpMethod.Get, url, null, managerToken)).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await _h.SendAsync(HttpMethod.Get, url, null, runner)).StatusCode); // run alone does not allow viewing
            Assert.Equal(HttpStatusCode.Forbidden, (await _h.SendAsync(HttpMethod.Get, url, null, healthOnly)).StatusCode);
        }

        Assert.Equal(HttpStatusCode.OK, (await _h.SendAsync(HttpMethod.Get, Base, null, viewer)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await _h.SendAsync(HttpMethod.Get, $"{Base}/status", null, viewer)).StatusCode);

        Assert.Equal(HttpStatusCode.Unauthorized, (await _h.SendAsync(HttpMethod.Post, Base, new { }, null)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await _h.SendAsync(HttpMethod.Post, Base, new { }, nobody)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await _h.SendAsync(HttpMethod.Post, Base, new { }, viewer)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await _h.SendAsync(HttpMethod.Post, Base, new { }, managerToken)).StatusCode);

        // The data inventory needs system.health.view.
        Assert.Equal(HttpStatusCode.Forbidden, (await _h.SendAsync(HttpMethod.Get, "/api/v1/system/data-inventory", null, nobody)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await _h.SendAsync(HttpMethod.Get, "/api/v1/system/data-inventory", null, viewer)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await _h.SendAsync(HttpMethod.Get, "/api/v1/system/data-inventory", null, healthOnly)).StatusCode);

        var holders = await WithDbAsync(async db =>
        {
            var ids = await db.Permissions.Where(p => p.Code == "system.backup.view" || p.Code == "system.backup.run").Select(p => p.Id).ToListAsync(Ct);
            Assert.Equal(2, ids.Count);
            return await db.RolePermissions.Where(rp => ids.Contains(rp.PermissionId)).Select(rp => rp.RoleId).Distinct().ToListAsync(Ct);
        });

        // Super Admin holds permissions by definition; of the seven default roles none has a stored row for them (the test roles above are custom ones).
        var defaultRoles = await WithDbAsync(db => db.Roles.AsNoTracking().Where(r => r.IsSystem).Select(r => r.Id).ToListAsync(Ct));
        Assert.Empty(holders.Intersect(defaultRoles));
    }

    [Fact]
    public async Task The_data_inventory_gives_row_counts_and_no_personal_data()
    {
        var admin = await _h.AdminAsync();
        var category = await _h.CategoryAsync(admin);
        await _h.ProductAsync(admin, category.Id, StoreHelper.Unique("Inventory Tee"), 900m, [("Red", "M", 5)], weightGrams: 300);
        var json = await _h.JsonAsync(HttpMethod.Get, "/api/v1/system/data-inventory", HttpStatusCode.OK, bearer: admin);

        var keys = json.GetProperty("tables").EnumerateArray().ToDictionary(t => t.GetProperty("key").GetString()!, t => t.GetProperty("rows").GetInt64());
        Assert.Equal(["customers", "orders", "products", "variants", "stockLevels", "invoices", "payments", "auditLogs"], keys.Keys);
        Assert.True(keys["products"] > 0);
        Assert.True(keys["variants"] > 0);
        Assert.True(keys["stockLevels"] > 0);
        Assert.True(keys["auditLogs"] > 0);
        Assert.All(keys.Values, v => Assert.True(v >= 0));
        Assert.True(json.GetProperty("uploadsBytes").GetInt64() >= 0);
        Assert.DoesNotContain("@", json.ToString());
    }

    [Fact]
    public async Task Retention_deletes_only_our_own_old_files_of_this_database_and_keeps_the_newest_verified_one()
    {
        Directory.CreateDirectory(factory.BackupFolder);
        await using var scope = factory.Services.CreateAsyncScope();
        var engine = scope.ServiceProvider.GetRequiredService<IBackupEngine>();
        var runner = scope.ServiceProvider.GetRequiredService<BackupRunner>();
        var db = await engine.GetDatabaseNameAsync(Ct);
        var now = DateTimeOffset.UtcNow;

        string Make(string name)
        {
            var path = Path.Combine(factory.BackupFolder, name);
            File.WriteAllText(path, "not a real backup");
            return path;
        }

        var old = Make(BackupNaming.FileName(db, now.AddDays(-40), Guid.NewGuid()));
        var oldAlso = Make(BackupNaming.FileName(db, now.AddDays(-20), Guid.NewGuid()));
        var fresh = Make(BackupNaming.FileName(db, now.AddDays(-2), Guid.NewGuid()));
        var otherDb = Make(BackupNaming.FileName("SomeOtherDb", now.AddDays(-90), Guid.NewGuid()));
        var notOurs = Make("juvo-backup_manual-copy.bak");
        var notes = Make("readme-old.txt");
        File.SetLastWriteTimeUtc(notes, now.AddDays(-400).UtcDateTime);
        File.SetLastWriteTimeUtc(notOurs, now.AddDays(-400).UtcDateTime);
        var sub = Directory.CreateDirectory(Path.Combine(factory.BackupFolder, "sub")).FullName;
        var inSub = Path.Combine(sub, BackupNaming.FileName(db, now.AddDays(-60), Guid.NewGuid()));
        File.WriteAllText(inSub, "x");

        // The newest verified backup on record is the old file: it must stay although it is past the retention.
        var keepName = Path.GetFileName(oldAlso);
        var runIds = await WithDbAsync(async ctx =>
        {
            var oldRun = new BackupRun(BackupTrigger.Schedule, null, now.AddDays(-40));
            oldRun.Succeed(now.AddDays(-40), Path.GetFileName(old), 17, false);
            var keepRun = new BackupRun(BackupTrigger.Schedule, null, now.AddDays(-20));
            keepRun.Succeed(now.AddDays(-20), keepName, 17, false);
            ctx.BackupRuns.AddRange(oldRun, keepRun);
            await ctx.SaveChangesAsync(Ct);
            return (oldRun.Id, keepRun.Id);
        });

        // Make sure no newer verified run overrides the "keep" choice for this assertion.
        await WithDbAsync(async ctx => await ctx.BackupRuns.Where(r => r.Verified && r.StartedAt > now.AddDays(-20).AddHours(1)).ExecuteDeleteAsync(Ct));

        var removed = await runner.ApplyRetentionAsync(Ct);

        Assert.Equal([Path.GetFileName(old)], removed);
        Assert.False(File.Exists(old));
        Assert.True(File.Exists(oldAlso));
        Assert.True(File.Exists(fresh));
        Assert.True(File.Exists(otherDb));
        Assert.True(File.Exists(notOurs));
        Assert.True(File.Exists(notes));
        Assert.True(File.Exists(inSub));

        var rows = await WithDbAsync(ctx => ctx.BackupRuns.AsNoTracking().Where(r => r.Id == runIds.Item1 || r.Id == runIds.Item2).ToListAsync(Ct));
        Assert.NotNull(rows.Single(r => r.Id == runIds.Item1).FileDeletedAt);
        Assert.Null(rows.Single(r => r.Id == runIds.Item2).FileDeletedAt);

        // The engine itself refuses names that are not ours, and paths.
        Assert.False(engine.DeleteFile("readme-old.txt"));
        Assert.False(engine.DeleteFile(@"..\" + Path.GetFileName(fresh)));
        Assert.False(engine.DeleteFile("juvo-backup_manual-copy.bak"));
        Assert.True(File.Exists(notes));
    }

    [Fact]
    public async Task The_nightly_worker_does_nothing_while_the_setting_is_off_and_takes_one_logged_backup_when_it_is_on_and_due()
    {
        factory.Gate.Open();
        await _h.AdminAsync();
        var at = new TimeOnly(2, 30);
        var clock = new FixedClock(BackupSchedule.LastDue(DateTimeOffset.UtcNow, at).AddMinutes(1));
        var worker = new BackupWorker(factory.Services.GetRequiredService<IServiceScopeFactory>(), factory.Services.GetRequiredService<BackupCoordinator>(),
            Options.Create(new BackupOptions { DailyAt = "02:30", WorkerEnabled = true }), clock, NullLogger<BackupWorker>.Instance);

        var scheduledBefore = await WithDbAsync(db => db.BackupRuns.CountAsync(r => r.Trigger == BackupTrigger.Schedule, Ct));

        // Default: the setting is off.
        Assert.Null(await worker.RunIfDueAsync(Ct));
        Assert.Equal(scheduledBefore, await WithDbAsync(db => db.BackupRuns.CountAsync(r => r.Trigger == BackupTrigger.Schedule, Ct)));

        await WithDbAsync(async db =>
        {
            db.PlatformSettings.Add(new PlatformSetting(BackupSettingKeys.Enabled, "true"));
            await db.SaveChangesAsync(Ct);
            return 0;
        });

        var run = await worker.RunIfDueAsync(Ct);

        Assert.NotNull(run);
        Assert.Equal(BackupTrigger.Schedule, run.Trigger);
        Assert.Equal(BackupStatus.Succeeded, run.Status);
        Assert.True(run.Verified);
        var jobs = await WithDbAsync(db => db.JobLogs.AsNoTracking().Where(j => j.JobName == "system.backup" && j.Reference == run.Id.ToString()).ToListAsync(Ct));
        Assert.Equal(JobStatus.Succeeded, Assert.Single(jobs).Status);

        // One attempt per night.
        Assert.Null(await worker.RunIfDueAsync(Ct));

        var admin = await _h.AdminAsync();
        var status = await _h.JsonAsync(HttpMethod.Get, $"{Base}/status", HttpStatusCode.OK, bearer: admin);
        Assert.True(status.GetProperty("enabled").GetBoolean());
        Assert.Equal(JsonValueKind.Null, status.GetProperty("nextScheduledAt").ValueKind); // the hosted worker is off in tests, so no next time is promised

        await WithDbAsync(async db =>
        {
            db.PlatformSettings.RemoveRange(await db.PlatformSettings.Where(s => s.Key == BackupSettingKeys.Enabled).ToListAsync(Ct));
            await db.SaveChangesAsync(Ct); // a normal save, so the settings cache is emptied at once
            return 0;
        });
    }

    [Fact]
    public async Task A_backup_whose_folder_cannot_be_created_fails_cleanly_without_a_file()
    {
        factory.Gate.Open();
        await _h.AdminAsync();
        var blocker = Path.Combine(Path.GetTempPath(), "juvo-backup-blocker-" + Guid.NewGuid().ToString("N"));
        File.WriteAllText(blocker, "a file where the folder should be");
        try
        {
            var options = Options.Create(new BackupOptions { Folder = Path.Combine(blocker, "sub") });
            await using var scope = factory.Services.CreateAsyncScope();
            var engine = ActivatorUtilities.CreateInstance<SqlServerBackupEngine>(scope.ServiceProvider, options);

            var result = await engine.BackupAsync(BackupNaming.FileName("JuvoDb", DateTimeOffset.UtcNow, Guid.NewGuid()), Ct);

            Assert.False(result.Written);
            Assert.False(result.Verified);
            Assert.NotNull(result.Error);
        }
        finally
        {
            File.Delete(blocker);
        }
    }
}
