using System.ComponentModel.DataAnnotations;

namespace Juvo.Infrastructure.Options;

/// <summary>Where uploaded files are stored (local disk by default). Section: <c>Storage</c>.</summary>
public sealed class StorageOptions
{
    public const string SectionName = "Storage";

    /// <summary>Absolute folder, or a path relative to the application content root.</summary>
    [Required]
    public string RootPath { get; set; } = "App_Data/uploads";

    /// <summary>URL prefix under which stored files are served.</summary>
    [Required]
    public string PublicBaseUrl { get; set; } = "/files";
}

/// <summary>Transactional outbox worker settings. Section: <c>Outbox</c>.</summary>
public sealed class OutboxOptions
{
    public const string SectionName = "Outbox";

    /// <summary>Turn the background worker off (tests, migration runs).</summary>
    public bool Enabled { get; set; } = true;

    [Range(1, 3600)]
    public int PollIntervalSeconds { get; set; } = 5;

    [Range(1, 500)]
    public int BatchSize { get; set; } = 20;

    [Range(1, 50)]
    public int MaxAttempts { get; set; } = 8;

    [Range(1, 3600)]
    public int BaseDelaySeconds { get; set; } = 5;

    [Range(1, 1440)]
    public int MaxDelayMinutes { get; set; } = 60;
}

/// <summary>Database behaviour. Section: <c>Database</c>. The connection string itself is <c>ConnectionStrings:Default</c>.</summary>
public sealed class DatabaseOptions
{
    public const string SectionName = "Database";

    /// <summary>Apply pending migrations at start-up. Intended for Development only; Production uses the migrate command.</summary>
    public bool AutoMigrate { get; set; }

    /// <summary>SQL Server compatibility level hint for EF Core (150 = SQL Server 2019, 160 = 2022).</summary>
    [Range(120, 200)]
    public int CompatibilityLevel { get; set; } = 150;
}
