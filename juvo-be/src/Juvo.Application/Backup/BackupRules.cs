using System.Globalization;
using System.Text.RegularExpressions;

namespace Juvo.Application.Backup;

/// <summary>Settings keys of the backup feature (runtime settings registry).</summary>
public static class BackupSettingKeys
{
    /// <summary>Take a nightly backup. Default off; the folder, time and retention come from appsettings (section <c>Backup</c>).</summary>
    public const string Enabled = "Backup.Enabled";

    public const string AuditEntity = "BackupRun";
    public const string JobName = "system.backup";
}

/// <summary>Backup configuration (section <c>Backup</c> of appsettings). Read-only at run time; only <c>Backup.Enabled</c> is a runtime setting.</summary>
public sealed class BackupOptions
{
    public const string SectionName = "Backup";

    /// <summary>Absolute folder, or a path relative to the application content root. SQL Server must be able to write here (in production the SQL Server service account).</summary>
    public string Folder { get; set; } = "App_Data/backups";

    /// <summary>Own backup files older than this many days are deleted after a successful run (the newest verified file is always kept).</summary>
    public int RetentionDays { get; set; } = 14;

    /// <summary>Time of the nightly backup, <c>HH:mm</c> in Asia/Dhaka.</summary>
    public string DailyAt { get; set; } = "02:30";

    /// <summary>Start the worker. Turn off in tests and migration runs; the nightly run also needs the <c>Backup.Enabled</c> setting.</summary>
    public bool WorkerEnabled { get; set; } = true;

    /// <summary>Longest a BACKUP or VERIFY statement may run.</summary>
    public int CommandTimeoutMinutes { get; set; } = 120;
}

/// <summary>File naming of the backups this service creates. The name never holds personal data: prefix, database name, UTC time stamp and the first 8 characters of the run id.</summary>
public static partial class BackupNaming
{
    private const string Prefix = "juvo-backup_";
    private const string StampFormat = "yyyyMMdd'T'HHmmss'Z'";

    [GeneratedRegex(@"^juvo-backup_(?<db>[A-Za-z0-9_]{1,64})_(?<ts>\d{8}T\d{6}Z)_(?<id>[0-9a-f]{8})\.bak$", RegexOptions.CultureInvariant)]
    private static partial Regex Pattern();

    /// <summary>The database name as it appears in file names: letters, digits and underscore only, at most 64 characters.</summary>
    public static string SafeDatabase(string database)
    {
        var safe = new string([.. database.Select(c => char.IsAsciiLetterOrDigit(c) || c == '_' ? c : '_')]);
        if (safe.Length == 0)
        {
            return "db";
        }

        return safe.Length > 64 ? safe[..64] : safe;
    }

    public static string FileName(string database, DateTimeOffset utcNow, Guid runId) =>
        $"{Prefix}{SafeDatabase(database)}_{utcNow.UtcDateTime.ToString(StampFormat, CultureInfo.InvariantCulture)}_{runId.ToString("N")[..8]}.bak";

    /// <summary>True when the name was produced by <see cref="FileName"/> (and gives the database and time it carries).</summary>
    public static bool TryParse(string? name, out string database, out DateTimeOffset createdUtc)
    {
        database = string.Empty;
        createdUtc = default;
        if (string.IsNullOrEmpty(name) || name.Length > 200 || Pattern().Match(name) is not { Success: true } m)
        {
            return false;
        }

        if (!DateTimeOffset.TryParseExact(m.Groups["ts"].Value, StampFormat, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out createdUtc))
        {
            return false;
        }

        database = m.Groups["db"].Value;
        return true;
    }
}

/// <summary>Which files the retention job may delete. It only ever picks names made by <see cref="BackupNaming"/> for this database, older than the retention, and never the file to keep.</summary>
public static class BackupRetention
{
    public static IReadOnlyList<string> SelectExpired(IEnumerable<string> fileNames, string database, DateTimeOffset now, int retentionDays, string? keepFileName)
    {
        var safeDb = BackupNaming.SafeDatabase(database);
        var cutoff = now.AddDays(-Math.Max(1, retentionDays));
        var expired = new List<string>();
        foreach (var name in fileNames)
        {
            if (!BackupNaming.TryParse(name, out var db, out var created) || !string.Equals(db, safeDb, StringComparison.Ordinal))
            {
                continue;
            }

            if (created < cutoff && !string.Equals(name, keepFileName, StringComparison.Ordinal))
            {
                expired.Add(name);
            }
        }

        return expired;
    }
}

/// <summary>The nightly time in Asia/Dhaka (UTC+06:00, no daylight saving). Pure functions so the worker's decisions can be tested.</summary>
public static class BackupSchedule
{
    public static readonly TimeSpan Dhaka = TimeSpan.FromHours(6);

    /// <summary>A missed run is still taken when the application is up within this many hours after the due time (a restart at 02:35 still takes the 02:30 backup).</summary>
    public const int CatchUpHours = 6;

    public static bool TryParseTime(string? text, out TimeOnly time) =>
        TimeOnly.TryParseExact(text?.Trim(), "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out time);

    /// <summary>The latest due time that is not after <paramref name="now"/>.</summary>
    public static DateTimeOffset LastDue(DateTimeOffset now, TimeOnly at)
    {
        var local = now.ToOffset(Dhaka);
        var today = new DateTimeOffset(local.Year, local.Month, local.Day, at.Hour, at.Minute, 0, Dhaka);
        return today <= now ? today : today.AddDays(-1);
    }

    /// <summary>The first due time after <paramref name="now"/>.</summary>
    public static DateTimeOffset Next(DateTimeOffset now, TimeOnly at) => LastDue(now, at).AddDays(1);

    /// <summary>Whether the nightly run should start now: the due time has passed within the catch-up window and no scheduled run has started since it.</summary>
    public static bool IsDue(DateTimeOffset now, TimeOnly at, DateTimeOffset? lastScheduledStart)
    {
        var due = LastDue(now, at);
        if (now - due > TimeSpan.FromHours(CatchUpHours))
        {
            return false;
        }

        return lastScheduledStart is null || lastScheduledStart < due;
    }
}
