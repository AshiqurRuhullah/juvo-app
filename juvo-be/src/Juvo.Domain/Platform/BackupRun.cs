using Juvo.Domain.Common;

namespace Juvo.Domain.Platform;

public enum BackupStatus
{
    Running = 0,
    Succeeded = 1,
    Failed = 2,
}

public enum BackupTrigger
{
    /// <summary>A staff member pressed "back up now".</summary>
    User = 1,

    /// <summary>The nightly worker.</summary>
    Schedule = 2,
}

/// <summary>
/// One database backup attempt (FR-AF03). Written when the run starts and finished when the file is written and verified. The file name never holds personal data
/// and the error text is stored already scrubbed. Not audited through the generic interceptor (manual runs get their own audit row).
/// </summary>
public sealed class BackupRun : Entity, INotAudited
{
    private BackupRun()
    {
    }

    public BackupRun(BackupTrigger trigger, Guid? triggeredByUserId, DateTimeOffset startedAt)
    {
        Trigger = trigger;
        TriggeredByUserId = triggeredByUserId;
        StartedAt = startedAt;
    }

    public BackupTrigger Trigger { get; private set; }

    public Guid? TriggeredByUserId { get; private set; }

    public BackupStatus Status { get; private set; } = BackupStatus.Running;

    public DateTimeOffset StartedAt { get; private set; }

    public DateTimeOffset? FinishedAt { get; private set; }

    public string? FileName { get; private set; }

    public long? SizeBytes { get; private set; }

    public bool Compressed { get; private set; }

    /// <summary><c>RESTORE VERIFYONLY ... WITH CHECKSUM</c> passed on the written file.</summary>
    public bool Verified { get; private set; }

    public string? Error { get; private set; }

    /// <summary>Set when the retention job removed the file. The row stays as history.</summary>
    public DateTimeOffset? FileDeletedAt { get; private set; }

    public void Succeed(DateTimeOffset now, string fileName, long sizeBytes, bool compressed)
    {
        Status = BackupStatus.Succeeded;
        FinishedAt = now;
        FileName = fileName;
        SizeBytes = sizeBytes;
        Compressed = compressed;
        Verified = true;
    }

    /// <summary>The backup ran but the file did not verify, or it did not run at all. A written but unverified file stays on disk for the operator to look at.</summary>
    public void Fail(DateTimeOffset now, string error, string? fileName = null, long? sizeBytes = null, bool compressed = false)
    {
        Status = BackupStatus.Failed;
        FinishedAt = now;
        FileName = fileName;
        SizeBytes = sizeBytes;
        Compressed = compressed;
        Verified = false;
        Error = error.Length > 2000 ? error[..2000] : error;
    }

    public void MarkFileDeleted(DateTimeOffset now) => FileDeletedAt = now;
}
