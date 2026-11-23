using Juvo.Application.Backup;
using Juvo.Infrastructure.Options;
using Juvo.Infrastructure.Persistence;
using Juvo.Infrastructure.Storage;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Juvo.Infrastructure.Backup;

/// <summary>
/// SQL Server backup of the application database (FR-AF03): <c>BACKUP DATABASE ... WITH CHECKSUM, INIT</c> (plus COMPRESSION where the edition allows it) followed by
/// <c>RESTORE VERIFYONLY ... WITH CHECKSUM</c>. It uses its own connection (BACKUP cannot run inside a transaction) and never builds SQL from file names: the database name is
/// quoted by the server (<c>QUOTENAME</c>) and the path travels as a parameter. SQL Server, not this process, writes the file, so the folder must be writable by the SQL Server
/// service account (LocalDB: the current Windows user).
/// </summary>
internal sealed class SqlServerBackupEngine(JuvoDbContext context, IOptions<BackupOptions> options, IOptions<StorageOptions> storage, IHostEnvironment environment, ILogger<SqlServerBackupEngine> logger)
    : IBackupEngine
{
    private const int CompressionNotSupportedError = 1844;
    private const int ExpressEngineEdition = 4;
    private const int MaxUploadFilesCounted = 200_000;

    public string Folder =>
        Path.GetFullPath(Path.IsPathRooted(options.Value.Folder) ? options.Value.Folder : Path.Combine(environment.ContentRootPath, options.Value.Folder));

    private SqlConnection NewConnection()
    {
        var connectionString = context.Database.GetConnectionString() ?? throw new InvalidOperationException("No connection string.");
        return new SqlConnection(connectionString);
    }

    private int CommandTimeoutSeconds => Math.Max(60, options.Value.CommandTimeoutMinutes * 60);

    public async Task<string> GetDatabaseNameAsync(CancellationToken cancellationToken)
    {
        await using var connection = NewConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = new SqlCommand("SELECT DB_NAME()", connection);
        return (string)(await command.ExecuteScalarAsync(cancellationToken))!;
    }

    public async Task<BackupEngineResult> BackupAsync(string fileName, CancellationToken cancellationToken)
    {
        if (!BackupNaming.TryParse(fileName, out _, out _))
        {
            return new BackupEngineResult(false, false, false, null, "The file name is not one this service creates.");
        }

        try
        {
            Directory.CreateDirectory(Folder);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new BackupEngineResult(false, false, false, null, $"The backup folder cannot be created: {ex.Message}");
        }

        var path = Path.Combine(Folder, fileName);
        await using var connection = NewConnection();
        await connection.OpenAsync(cancellationToken);

        var compressed = await SupportsCompressionAsync(connection, cancellationToken);
        try
        {
            await RunBackupAsync(connection, path, compressed, cancellationToken);
        }
        catch (SqlException ex) when (compressed && ex.Number == CompressionNotSupportedError)
        {
            logger.LogInformation("This SQL Server edition does not support backup compression; backing up without it.");
            compressed = false;
            await RunBackupAsync(connection, path, compressed, cancellationToken);
        }
        catch (SqlException ex)
        {
            return new BackupEngineResult(false, false, compressed, null, $"BACKUP DATABASE failed: {ex.Message}");
        }

        long? size = null;
        try
        {
            size = new FileInfo(path) is { Exists: true } info ? info.Length : null;
        }
        catch (IOException)
        {
            // The size is informational: SQL Server may have written to a place this process cannot read.
        }

        try
        {
            await using var verify = new SqlCommand("RESTORE VERIFYONLY FROM DISK = @path WITH CHECKSUM", connection) { CommandTimeout = CommandTimeoutSeconds };
            verify.Parameters.Add(new SqlParameter("@path", System.Data.SqlDbType.NVarChar, 1000) { Value = path });
            await verify.ExecuteNonQueryAsync(cancellationToken);
        }
        catch (SqlException ex)
        {
            return new BackupEngineResult(true, false, compressed, size, $"RESTORE VERIFYONLY failed: {ex.Message}");
        }

        return new BackupEngineResult(true, true, compressed, size, null);
    }

    private static async Task<bool> SupportsCompressionAsync(SqlConnection connection, CancellationToken cancellationToken)
    {
        // Express (and therefore LocalDB) cannot create compressed backups. Anything else is tried and falls back on error 1844.
        await using var command = new SqlCommand("SELECT CAST(SERVERPROPERTY('EngineEdition') AS int)", connection);
        return (await command.ExecuteScalarAsync(cancellationToken)) is int edition && edition != ExpressEngineEdition;
    }

    private async Task RunBackupAsync(SqlConnection connection, string path, bool compression, CancellationToken cancellationToken)
    {
        var with = compression ? "CHECKSUM, COMPRESSION, INIT" : "CHECKSUM, INIT";
        var sql = $"""
            DECLARE @sql nvarchar(max) = N'BACKUP DATABASE ' + QUOTENAME(DB_NAME()) + N' TO DISK = @path WITH {with}';
            EXEC sp_executesql @sql, N'@path nvarchar(1000)', @path = @p;
            """;
        await using var command = new SqlCommand(sql, connection) { CommandTimeout = CommandTimeoutSeconds };
        command.Parameters.Add(new SqlParameter("@p", System.Data.SqlDbType.NVarChar, 1000) { Value = path });
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public IReadOnlyList<BackupFileInfo> ListFiles()
    {
        if (!Directory.Exists(Folder))
        {
            return [];
        }

        try
        {
            return [.. new DirectoryInfo(Folder).EnumerateFiles().Select(f => new BackupFileInfo(f.Name, f.Length))];
        }
        catch (IOException)
        {
            return [];
        }
    }

    public bool DeleteFile(string fileName)
    {
        // Defence in depth: only a bare file name in our own pattern is ever deleted, never a path.
        if (!BackupNaming.TryParse(fileName, out _, out _) || fileName != Path.GetFileName(fileName))
        {
            return false;
        }

        var path = Path.Combine(Folder, fileName);
        try
        {
            if (!File.Exists(path) || new FileInfo(path).Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                return false;
            }

            File.Delete(path);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Could not delete the expired backup file {File}.", fileName);
            return false;
        }
    }

    public long? FreeSpaceBytes()
    {
        try
        {
            var root = Path.GetPathRoot(Folder);
            return string.IsNullOrEmpty(root) || root.StartsWith(@"\\", StringComparison.Ordinal) ? null : new DriveInfo(root).AvailableFreeSpace;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public UploadsUsage MeasureUploads()
    {
        var root = StorageLocation.ResolveRoot(storage.Value, environment);
        if (!Directory.Exists(root))
        {
            return new UploadsUsage(0, 0, false);
        }

        long bytes = 0;
        var files = 0;
        try
        {
            foreach (var file in new DirectoryInfo(root).EnumerateFiles("*", SearchOption.AllDirectories))
            {
                if (files >= MaxUploadFilesCounted)
                {
                    return new UploadsUsage(bytes, files, true);
                }

                bytes += file.Length;
                files++;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new UploadsUsage(bytes, files, true);
        }

        return new UploadsUsage(bytes, files, false);
    }
}
