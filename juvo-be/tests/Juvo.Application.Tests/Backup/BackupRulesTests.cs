using Juvo.Application.Backup;
using Juvo.Application.Monitoring;

namespace Juvo.Application.Tests.Backup;

public class BackupRulesTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 23, 8, 0, 0, TimeSpan.Zero);
    private const string Db = "JuvoDb";

    private static string Name(string db, DateTimeOffset at, string id = "0a1b2c3d") =>
        BackupNaming.FileName(db, at, Guid.Parse(id + "-0000-0000-0000-000000000000"));

    [Fact]
    public void File_names_follow_one_pattern_and_hold_no_personal_data()
    {
        var name = Name(Db, new DateTimeOffset(2026, 11, 20, 20, 30, 5, TimeSpan.Zero));

        Assert.Equal("juvo-backup_JuvoDb_20261120T203005Z_0a1b2c3d.bak", name);
        Assert.True(BackupNaming.TryParse(name, out var db, out var created));
        Assert.Equal(Db, db);
        Assert.Equal(new DateTimeOffset(2026, 11, 20, 20, 30, 5, TimeSpan.Zero), created);
    }

    [Fact]
    public void An_unusual_database_name_is_made_safe_and_still_parses()
    {
        var name = Name("Juvo DB;DROP-x", Now);

        Assert.DoesNotContain(' ', name);
        Assert.DoesNotContain(';', name);
        Assert.True(BackupNaming.TryParse(name, out var db, out _));
        Assert.Equal("Juvo_DB_DROP_x", db);
        Assert.Equal(64, BackupNaming.SafeDatabase(new string('a', 100)).Length);
    }

    [Theory]
    [InlineData("notes.txt")]
    [InlineData("juvo-backup_JuvoDb_20261120T203005Z_0a1b2c3d.BAK")]
    [InlineData("juvo-backup_JuvoDb_20261120T203005Z_0a1b2c3d.bak.zip")]
    [InlineData("juvo-backup_JuvoDb_20261320T203005Z_0a1b2c3d.bak")]
    [InlineData("juvo-backup_JuvoDb_20261120T203005Z_ZZ.bak")]
    [InlineData("..\\juvo-backup_JuvoDb_20261120T203005Z_0a1b2c3d.bak")]
    [InlineData("juvo-backup_JuvoDb_20261120T203005Z_0a1b2c3d.bak\n")]
    [InlineData("")]
    public void Names_that_are_not_ours_do_not_match(string name) => Assert.False(BackupNaming.TryParse(name, out _, out _));

    [Fact]
    public void Retention_picks_only_our_own_old_files_of_this_database()
    {
        var old1 = Name(Db, Now.AddDays(-15), "aaaaaaa1");
        var old2 = Name(Db, Now.AddDays(-40), "aaaaaaa2");
        var fresh = Name(Db, Now.AddDays(-3), "aaaaaaa3");
        var edge = Name(Db, Now.AddDays(-14).AddMinutes(1), "aaaaaaa4");
        var otherDb = Name("OtherDb", Now.AddDays(-90), "aaaaaaa5");
        string[] files = [old1, old2, fresh, edge, otherDb, "report-2020.pdf", "old-notes.bak", "juvo-backup_JuvoDb_20200101T000000Z_xx.bak"];

        var expired = BackupRetention.SelectExpired(files, Db, Now, 14, keepFileName: null);

        Assert.Equal(new[] { old1, old2 }.OrderBy(n => n, StringComparer.Ordinal), expired.OrderBy(n => n, StringComparer.Ordinal));
    }

    [Fact]
    public void The_newest_verified_file_is_never_selected_even_when_it_is_old()
    {
        var old = Name(Db, Now.AddDays(-30), "bbbbbbb1");
        var older = Name(Db, Now.AddDays(-31), "bbbbbbb2");

        var expired = BackupRetention.SelectExpired([old, older], Db, Now, 14, keepFileName: old);

        Assert.Equal([older], expired);
    }

    [Fact]
    public void Retention_below_one_day_is_treated_as_one_day()
    {
        var yesterdayish = Name(Db, Now.AddHours(-30), "ccccccc1");
        var recent = Name(Db, Now.AddHours(-2), "ccccccc2");

        Assert.Equal([yesterdayish], BackupRetention.SelectExpired([yesterdayish, recent], Db, Now, 0, null));
    }

    [Fact]
    public void The_due_time_is_computed_in_dhaka_time()
    {
        Assert.True(BackupSchedule.TryParseTime("02:30", out var at));

        // 2026-11-23 02:29 in Dhaka (UTC+6) is 20:29 UTC on the 22nd: the previous run was 2026-11-22 02:30 Dhaka.
        var before = new DateTimeOffset(2026, 11, 22, 20, 29, 0, TimeSpan.Zero);
        Assert.Equal(new DateTimeOffset(2026, 11, 22, 2, 30, 0, BackupSchedule.Dhaka), BackupSchedule.LastDue(before, at));
        Assert.Equal(new DateTimeOffset(2026, 11, 23, 2, 30, 0, BackupSchedule.Dhaka), BackupSchedule.Next(before, at));

        // Exactly at the due time it counts as due, and the next one is a day later.
        var exact = new DateTimeOffset(2026, 11, 22, 20, 30, 0, TimeSpan.Zero);
        Assert.Equal(exact, BackupSchedule.LastDue(exact, at));
        Assert.Equal(exact.AddDays(1), BackupSchedule.Next(exact, at));

        // The same instant read in another offset gives the same answer.
        Assert.Equal(BackupSchedule.LastDue(before, at), BackupSchedule.LastDue(before.ToOffset(TimeSpan.FromHours(-5)), at));
    }

    [Theory]
    [InlineData("02:30", true)]
    [InlineData("00:00", true)]
    [InlineData("23:59", true)]
    [InlineData("2:30", false)]
    [InlineData("24:00", false)]
    [InlineData("02:60", false)]
    [InlineData("", false)]
    [InlineData("noon", false)]
    public void Only_a_clock_time_written_hh_mm_is_accepted(string text, bool ok) => Assert.Equal(ok, BackupSchedule.TryParseTime(text, out _));

    [Fact]
    public void The_nightly_run_is_due_once_per_night_within_the_catch_up_window()
    {
        Assert.True(BackupSchedule.TryParseTime("02:30", out var at));
        var due = new DateTimeOffset(2026, 11, 23, 2, 30, 0, BackupSchedule.Dhaka);

        Assert.False(BackupSchedule.IsDue(due.AddMinutes(-1), at, lastScheduledStart: due.AddDays(-1).AddSeconds(30))); // not yet
        Assert.True(BackupSchedule.IsDue(due.AddMinutes(1), at, due.AddDays(-1).AddSeconds(30)));                       // due
        Assert.True(BackupSchedule.IsDue(due.AddMinutes(1), at, null));                                                 // never ran
        Assert.False(BackupSchedule.IsDue(due.AddMinutes(2), at, due.AddMinutes(1)));                                   // already taken tonight (also when it failed)
        Assert.True(BackupSchedule.IsDue(due.AddHours(BackupSchedule.CatchUpHours), at, null));                         // restart soon after still catches up
        Assert.False(BackupSchedule.IsDue(due.AddHours(BackupSchedule.CatchUpHours).AddMinutes(1), at, null));          // too late: waits for the next night
    }

    [Fact]
    public void Error_texts_of_a_failed_backup_are_scrubbed()
    {
        var text = ErrorScrubber.Scrub("BACKUP DATABASE failed: Cannot open backup device. password=Sup3rSecret! contact owner@example.com +8801712345678\n   at Juvo.Something.Run()");

        Assert.NotNull(text);
        Assert.DoesNotContain("Sup3rSecret", text);
        Assert.DoesNotContain("owner@example.com", text);
        Assert.DoesNotContain("1712345678", text);
        Assert.DoesNotContain("Juvo.Something", text);
        Assert.Contains("BACKUP DATABASE failed", text);
    }
}
