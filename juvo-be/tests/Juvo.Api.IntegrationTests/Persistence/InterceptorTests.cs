using System.Text.Json;
using Juvo.Api.IntegrationTests.Support;
using Juvo.Domain.Platform;
using Microsoft.EntityFrameworkCore;

namespace Juvo.Api.IntegrationTests.Persistence;

public class InterceptorTests(TestDbFixture fixture) : IClassFixture<TestDbFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<List<AuditLog>> AuditFor(TestDbContext db, Guid entityId) =>
        await db.AuditLogs.Where(a => a.EntityId == entityId.ToString()).OrderBy(a => a.OccurredAt).ThenBy(a => a.Id).ToListAsync(Ct);

    [Fact]
    public async Task Insert_stamps_created_and_writes_an_audit_row_without_ignored_properties()
    {
        await using var db = fixture.NewContext();
        var widget = new Widget("Red dress", 1200m, "top-secret");
        db.Widgets.Add(widget);

        await db.SaveChangesAsync(Ct);

        Assert.Equal(fixture.Time.GetUtcNow(), widget.CreatedAt);
        Assert.Equal(fixture.User.UserId, widget.CreatedBy);

        var audit = Assert.Single(await AuditFor(db, widget.Id));
        Assert.Equal(AuditAction.Created, audit.Action);
        Assert.Equal("Widget", audit.Entity);
        Assert.Equal(fixture.User.BranchId, audit.BranchId);
        Assert.Equal(fixture.User.UserId, audit.UserId);
        Assert.Equal("corr-test-1", audit.CorrelationId);
        Assert.Equal("10.0.0.1", audit.IpAddress);
        using var json = JsonDocument.Parse(audit.NewValue!);
        Assert.Equal("Red dress", json.RootElement.GetProperty("Name").GetString());
        Assert.False(json.RootElement.TryGetProperty("Secret", out _), "[AuditIgnore] property must not be written to the audit log");
        Assert.Null(audit.OldValue);
    }

    [Fact]
    public async Task Update_audits_only_changed_properties_with_old_and_new_values()
    {
        Guid id;
        await using (var setup = fixture.NewContext())
        {
            var w = new Widget("Blue purse", 500m, "s");
            setup.Widgets.Add(w);
            await setup.SaveChangesAsync(Ct);
            id = w.Id;
        }

        fixture.Time.Advance(TimeSpan.FromMinutes(5));
        await using var db = fixture.NewContext();
        var widget = await db.Widgets.SingleAsync(w => w.Id == id, Ct);
        widget.Reprice(650m);
        await db.SaveChangesAsync(Ct);

        Assert.Equal(fixture.Time.GetUtcNow(), widget.UpdatedAt);
        var audits = await AuditFor(db, id);
        var update = audits.Single(a => a.Action == AuditAction.Updated);
        using var oldJson = JsonDocument.Parse(update.OldValue!);
        using var newJson = JsonDocument.Parse(update.NewValue!);
        Assert.Equal(500m, oldJson.RootElement.GetProperty("Price").GetDecimal());
        Assert.Equal(650m, newJson.RootElement.GetProperty("Price").GetDecimal());
        Assert.False(newJson.RootElement.TryGetProperty("Name", out _), "unchanged properties must not be listed");
    }

    [Fact]
    public async Task Save_without_changes_writes_no_audit_row()
    {
        Guid id;
        await using (var setup = fixture.NewContext())
        {
            var w = new Widget("Shoes", 900m, "s");
            setup.Widgets.Add(w);
            await setup.SaveChangesAsync(Ct);
            id = w.Id;
        }

        await using var db = fixture.NewContext();
        _ = await db.Widgets.SingleAsync(w => w.Id == id, Ct);
        await db.SaveChangesAsync(Ct);

        Assert.Single(await AuditFor(db, id)); // only the Created row
    }

    [Fact]
    public async Task Remove_is_a_soft_delete_hidden_by_the_global_filter_and_audited()
    {
        Guid id;
        await using (var setup = fixture.NewContext())
        {
            var w = new Widget("Old scarf", 100m, "s");
            setup.Widgets.Add(w);
            await setup.SaveChangesAsync(Ct);
            id = w.Id;
        }

        await using (var db = fixture.NewContext())
        {
            var widget = await db.Widgets.SingleAsync(w => w.Id == id, Ct);
            db.Widgets.Remove(widget);
            await db.SaveChangesAsync(Ct);
        }

        await using var check = fixture.NewContext();
        Assert.False(await check.Widgets.AnyAsync(w => w.Id == id, Ct), "soft-deleted rows are hidden by default");
        var row = await check.Widgets.IgnoreQueryFilters().SingleAsync(w => w.Id == id, Ct);
        Assert.True(row.IsDeleted);
        Assert.NotNull(row.DeletedAt);
        Assert.Equal(fixture.User.UserId, row.DeletedBy);
        Assert.Contains(await AuditFor(check, id), a => a.Action == AuditAction.Deleted);
    }

    [Fact]
    public async Task Append_only_tables_reject_updates_and_deletes()
    {
        Guid id;
        await using (var setup = fixture.NewContext())
        {
            var entry = new LedgerEntry(250m);
            setup.Ledger.Add(entry);
            await setup.SaveChangesAsync(Ct);
            id = entry.Id;
        }

        await using (var update = fixture.NewContext())
        {
            var entry = await update.Ledger.SingleAsync(e => e.Id == id, Ct);
            entry.Change(1m);
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => update.SaveChangesAsync(Ct));
            Assert.Contains("append-only", ex.Message, StringComparison.Ordinal);
        }

        await using var delete = fixture.NewContext();
        var toDelete = await delete.Ledger.SingleAsync(e => e.Id == id, Ct);
        delete.Ledger.Remove(toDelete);
        await Assert.ThrowsAsync<InvalidOperationException>(() => delete.SaveChangesAsync(Ct));
    }

    [Fact]
    public async Task Two_users_editing_the_same_row_get_a_concurrency_conflict()
    {
        Guid id;
        await using (var setup = fixture.NewContext())
        {
            var w = new Widget("Last unit", 10m, "s");
            setup.Widgets.Add(w);
            await setup.SaveChangesAsync(Ct);
            id = w.Id;
        }

        await using var first = fixture.NewContext();
        await using var second = fixture.NewContext();
        var a = await first.Widgets.SingleAsync(w => w.Id == id, Ct);
        var b = await second.Widgets.SingleAsync(w => w.Id == id, Ct);
        a.Reprice(11m);
        b.Reprice(12m);

        await first.SaveChangesAsync(Ct);

        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => second.SaveChangesAsync(Ct));
    }

    [Fact]
    public async Task Domain_events_are_written_to_the_outbox_in_the_same_save()
    {
        Guid id;
        await using (var setup = fixture.NewContext())
        {
            var w = new Widget("Evented", 10m, "s");
            setup.Widgets.Add(w);
            await setup.SaveChangesAsync(Ct);
            id = w.Id;
        }

        await using var db = fixture.NewContext();
        var widget = await db.Widgets.SingleAsync(w => w.Id == id, Ct);
        widget.Rename("Evented v2", fixture.Time.GetUtcNow());

        await db.SaveChangesAsync(Ct);

        Assert.Empty(widget.DomainEvents);
        var message = await db.OutboxMessages.SingleAsync(m => m.Payload.Contains(id.ToString()), Ct);
        Assert.Equal("domain.WidgetRenamed", message.Type);
        Assert.Equal(OutboxStatus.Pending, message.Status);
        Assert.Equal("corr-test-1", message.CorrelationId);
        using var payload = JsonDocument.Parse(message.Payload);
        Assert.Equal("Evented v2", payload.RootElement.GetProperty("newName").GetString());
    }

    [Fact]
    public async Task Failed_save_rolls_back_both_the_entity_and_its_outbox_row()
    {
        await using var db = fixture.NewContext();
        var widget = new Widget(new string('n', 150), 1m, "s"); // Name is limited to 100 characters
        widget.Rename(new string('n', 150), fixture.Time.GetUtcNow());
        db.Widgets.Add(widget);

        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync(Ct));

        await using var check = fixture.NewContext();
        Assert.False(await check.Widgets.IgnoreQueryFilters().AnyAsync(w => w.Id == widget.Id, Ct));
        Assert.False(await check.OutboxMessages.AnyAsync(m => m.Payload.Contains(widget.Id.ToString()), Ct));
        Assert.Empty(await AuditFor(check, widget.Id));
    }
}
