using Juvo.Api.IntegrationTests.Support;
using Juvo.Application.Abstractions;
using Juvo.Domain.Platform;
using Juvo.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Juvo.Api.IntegrationTests.Persistence;

/// <summary>
/// The data layer half of branch isolation, run against real SQL Server: a global query filter hides other branches' rows and a
/// save guard refuses writes into other branches, so a developer cannot forget the check in a query or a command.
/// </summary>
public class BranchScopeTests(TestDbFixture fixture) : IClassFixture<TestDbFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly Guid BranchA = Guid.NewGuid();
    private static readonly Guid BranchB = Guid.NewGuid();

    /// <summary>Notes in both branches, written without any scope (like a system job or a global user).</summary>
    private async Task<(Guid InA, Guid InB)> SeedAsync()
    {
        await using var db = fixture.NewContext();
        var a = new BranchNote(BranchA, "note in A");
        var b = new BranchNote(BranchB, "note in B");
        db.BranchNotes.AddRange(a, b);
        await db.SaveChangesAsync(Ct);
        return (a.Id, b.Id);
    }

    [Fact]
    public async Task A_scoped_user_reads_only_rows_of_their_own_branch()
    {
        var (inA, inB) = await SeedAsync();

        await using var db = fixture.NewContext(FakeBranchContext.Scoped(BranchA));
        var visible = await db.BranchNotes.Select(n => n.Id).ToListAsync(Ct);

        Assert.Contains(inA, visible);
        Assert.DoesNotContain(inB, visible);
        Assert.False(await db.BranchNotes.AnyAsync(n => n.Id == inB, Ct), "a direct lookup of another branch's row finds nothing");
        Assert.Null(await db.BranchNotes.FindAsync([inB], Ct));
    }

    [Fact]
    public async Task A_user_with_two_branches_sees_both_and_not_a_third()
    {
        var (inA, inB) = await SeedAsync();
        var third = Guid.NewGuid();
        await using (var setup = fixture.NewContext())
        {
            setup.BranchNotes.Add(new BranchNote(third, "note in C"));
            await setup.SaveChangesAsync(Ct);
        }

        await using var db = fixture.NewContext(FakeBranchContext.Scoped(BranchA, BranchB));
        var texts = await db.BranchNotes.Select(n => n.Text).ToListAsync(Ct);

        Assert.Contains("note in A", texts);
        Assert.Contains("note in B", texts);
        Assert.DoesNotContain("note in C", texts);
        Assert.NotEqual(inA, inB);
    }

    [Fact]
    public async Task Unscoped_callers_see_every_branch_and_ignoring_the_filter_is_explicit()
    {
        var (inA, inB) = await SeedAsync();

        await using var unscoped = fixture.NewContext(FakeBranchContext.Unscoped());
        var all = await unscoped.BranchNotes.Select(n => n.Id).ToListAsync(Ct);
        Assert.Contains(inA, all);
        Assert.Contains(inB, all);

        await using var scoped = fixture.NewContext(FakeBranchContext.Scoped(BranchA));
        var bypass = await scoped.BranchNotes.IgnoreQueryFilters([ModelDefaults.BranchScopeFilter]).Select(n => n.Id).ToListAsync(Ct);
        Assert.Contains(inB, bypass); // visible only because the code says so, on purpose
    }

    [Fact]
    public async Task A_scoped_user_cannot_write_into_another_branch()
    {
        await using var db = fixture.NewContext(FakeBranchContext.Scoped(BranchA));
        db.BranchNotes.Add(new BranchNote(BranchB, "sneaky"));

        var denied = await Assert.ThrowsAsync<BranchAccessDeniedException>(() => db.SaveChangesAsync(Ct));

        Assert.Equal(BranchB, denied.BranchId);
        Assert.Equal("BranchNote", denied.Entity);
        await using var check = fixture.NewContext();
        Assert.False(await check.BranchNotes.AnyAsync(n => n.Text == "sneaky", Ct));
    }

    [Fact]
    public async Task A_scoped_user_can_write_into_their_own_branch_but_not_move_a_row_out()
    {
        await using (var ok = fixture.NewContext(FakeBranchContext.Scoped(BranchA)))
        {
            ok.BranchNotes.Add(new BranchNote(BranchA, "mine"));
            await ok.SaveChangesAsync(Ct);
        }

        await using var db = fixture.NewContext(FakeBranchContext.Scoped(BranchA));
        var note = await db.BranchNotes.FirstAsync(n => n.Text == "mine", Ct);
        note.MoveTo(BranchB);

        await Assert.ThrowsAsync<BranchAccessDeniedException>(() => db.SaveChangesAsync(Ct));
    }

    [Fact]
    public async Task A_row_of_another_branch_cannot_be_updated_or_deleted_even_if_it_was_loaded_without_the_filter()
    {
        var (_, inB) = await SeedAsync();

        await using var db = fixture.NewContext(FakeBranchContext.Scoped(BranchA));
        var smuggled = await db.BranchNotes.IgnoreQueryFilters().FirstAsync(n => n.Id == inB, Ct);
        db.BranchNotes.Remove(smuggled);

        await Assert.ThrowsAsync<BranchAccessDeniedException>(() => db.SaveChangesAsync(Ct));

        await using var check = fixture.NewContext();
        Assert.True(await check.BranchNotes.AnyAsync(n => n.Id == inB, Ct));
    }

    [Fact]
    public async Task Global_and_system_callers_may_write_anywhere()
    {
        await using var db = fixture.NewContext(FakeBranchContext.Unscoped());
        db.BranchNotes.AddRange(new BranchNote(BranchA, "global to A"), new BranchNote(BranchB, "global to B"));

        await db.SaveChangesAsync(Ct);

        Assert.Equal(2, await db.BranchNotes.CountAsync(n => n.Text.StartsWith("global to"), Ct));
    }

    [Fact]
    public async Task A_scoped_user_with_no_branches_sees_and_writes_nothing()
    {
        await SeedAsync();
        await using var db = fixture.NewContext(FakeBranchContext.Scoped());

        Assert.Empty(await db.BranchNotes.ToListAsync(Ct));
        db.BranchNotes.Add(new BranchNote(BranchA, "nope"));
        await Assert.ThrowsAsync<BranchAccessDeniedException>(() => db.SaveChangesAsync(Ct));
    }

    [Fact]
    public async Task Audit_rows_record_the_active_branch()
    {
        fixture.User.BranchId = BranchA;
        Guid id;
        await using (var db = fixture.NewContext(FakeBranchContext.Scoped(BranchA)))
        {
            var note = new BranchNote(BranchA, "audited note");
            db.BranchNotes.Add(note);
            await db.SaveChangesAsync(Ct);
            id = note.Id;
        }

        await using var check = fixture.NewContext();
        var audit = await check.AuditLogs.SingleAsync(a => a.EntityId == id.ToString(), Ct);
        Assert.Equal(BranchA, audit.BranchId);
        Assert.Equal(AuditAction.Created, audit.Action);
    }
}
