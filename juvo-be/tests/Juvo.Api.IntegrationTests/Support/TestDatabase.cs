using Microsoft.Data.SqlClient;

namespace Juvo.Api.IntegrationTests.Support;

/// <summary>
/// Integration tests use a real local SQL Server (no Docker). Every test class gets its own database
/// (<c>JuvoDb_Test_&lt;guid&gt;</c>) which is created from the migrations and dropped afterwards.
/// Server: environment variable <c>JUVO_TEST_SQL_SERVER</c>, default LocalDB.
/// </summary>
internal static class TestDatabase
{
    private const string DefaultServer = "(localdb)\\MSSQLLocalDB";

    private static string Server => Environment.GetEnvironmentVariable("JUVO_TEST_SQL_SERVER") ?? DefaultServer;

    public static string NewConnectionString() =>
        $"Server={Server};Database=JuvoDb_Test_{Guid.NewGuid():N};Trusted_Connection=True;TrustServerCertificate=True";

    public static async Task DropAsync(string connectionString)
    {
        var builder = new SqlConnectionStringBuilder(connectionString);
        var database = builder.InitialCatalog;
        if (!database.StartsWith("JuvoDb_Test_", StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Refusing to drop '{database}': only JuvoDb_Test_* databases may be dropped by tests.");
        }

        builder.InitialCatalog = "master";
        SqlConnection.ClearAllPools();
        await using var connection = new SqlConnection(builder.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            IF DB_ID(N'{database}') IS NOT NULL
            BEGIN
                ALTER DATABASE [{database}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
                DROP DATABASE [{database}];
            END
            """;
        await command.ExecuteNonQueryAsync();
    }
}
