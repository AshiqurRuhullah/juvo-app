using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Juvo.Infrastructure.Persistence;

/// <summary>
/// Used only by <c>dotnet ef</c> at design time, so migrations do not need the web host.
/// Connection: environment variable <c>JUVO_CONNECTION</c>, or the local default (LocalDB, Windows authentication).
/// </summary>
internal sealed class JuvoDbContextFactory : IDesignTimeDbContextFactory<JuvoDbContext>
{
    public JuvoDbContext CreateDbContext(string[] args)
    {
        var connection = Environment.GetEnvironmentVariable("JUVO_CONNECTION")
            ?? "Server=(localdb)\\MSSQLLocalDB;Database=JuvoDb;Trusted_Connection=True;TrustServerCertificate=True;MultipleActiveResultSets=true";

        var builder = new DbContextOptionsBuilder<JuvoDbContext>();
        builder.UseSqlServer(connection, sql =>
        {
            sql.MigrationsAssembly(typeof(JuvoDbContext).Assembly.GetName().Name);
            sql.UseCompatibilityLevel(150);
        });
        return new JuvoDbContext(builder.Options);
    }
}
