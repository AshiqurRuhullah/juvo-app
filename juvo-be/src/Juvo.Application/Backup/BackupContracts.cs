using Juvo.Domain.Platform;

namespace Juvo.Application.Backup;

/// <summary>Outcome of the engine: the file was written, and whether it verified.</summary>
public sealed record BackupEngineResult(bool Written, bool Verified, bool Compressed, long? SizeBytes, string? Error);

public sealed record BackupFileInfo(string Name, long SizeBytes);

public sealed record UploadsUsage(long Bytes, int Files, bool Truncated);

/// <summary>
/// The database side of backups. The SQL Server implementation lives in Infrastructure (BACKUP DATABASE, RESTORE VERIFYONLY, file clean-up). Nothing here serves a file over HTTP.
/// </summary>
public interface IBackupEngine
{
    /// <summary>The backup folder as an absolute path.</summary>
    string Folder { get; }

    Task<string> GetDatabaseNameAsync(CancellationToken cancellationToken);

    /// <summary>Back up to <c>Folder/fileName</c> with CHECKSUM (and COMPRESSION where the edition allows it), then run RESTORE VERIFYONLY on the file.</summary>
    Task<BackupEngineResult> BackupAsync(string fileName, CancellationToken cancellationToken);

    /// <summary>Files of the folder (not sub-folders), name and size.</summary>
    IReadOnlyList<BackupFileInfo> ListFiles();

    /// <summary>Delete one file of the folder by name. Refuses any name that is not one of this service's own backup names.</summary>
    bool DeleteFile(string fileName);

    /// <summary>Free bytes on the drive of the folder, or null when it cannot be read cheaply (network path, folder missing).</summary>
    long? FreeSpaceBytes();

    /// <summary>Size of the uploaded files folder, counting at most a fixed number of files.</summary>
    UploadsUsage MeasureUploads();
}

public sealed record BackupRunDto(
    Guid Id, BackupTrigger Trigger, Guid? TriggeredByUserId, BackupStatus Status, DateTimeOffset StartedAt, DateTimeOffset? FinishedAt, int? DurationSeconds, string? FileName,
    long? SizeBytes, bool Compressed, bool Verified, string? Error, DateTimeOffset? FileDeletedAt);

public sealed record BackupStatusDto(
    bool Enabled, string Folder, int RetentionDays, string DailyAt, string TimeZone, DateTimeOffset? NextScheduledAt, bool Running, BackupRunDto? LastRun, DateTimeOffset? LastVerifiedAt,
    double? LastVerifiedAgeHours, string? LastVerifiedFileName, long? FolderFreeBytes);

public sealed record DataInventoryDto(DateTimeOffset At, IReadOnlyList<DataInventoryRow> Tables, long? UploadsBytes, int? UploadsFiles, bool UploadsTruncated);

public sealed record DataInventoryRow(string Key, long Rows);
