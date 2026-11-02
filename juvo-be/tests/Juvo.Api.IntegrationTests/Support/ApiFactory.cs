using Juvo.Infrastructure.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Juvo.Api.IntegrationTests.Support;

/// <summary>
/// Hosts the real API in memory against a throw-away SQL Server database created from the migrations.
/// Settings are passed with <c>UseSetting</c> so they are visible before the app reads configuration.
/// </summary>
public class ApiFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    private readonly string _connectionString = TestDatabase.NewConnectionString();
    private readonly string _storageRoot = Path.Combine(Path.GetTempPath(), "juvo-tests-" + Guid.NewGuid().ToString("N"));
    private readonly Dictionary<string, string> _settings = new()
    {
        ["Outbox:Enabled"] = "false",
        ["OpenApi:Enabled"] = "true",
        ["Database:AutoMigrate"] = "false",
    };

    public string ConnectionString => _connectionString;

    protected void Setting(string key, string value) => _settings[key] = value;

    protected virtual void ConfigureSettings()
    {
    }

    /// <summary>Register extra services (test handlers, fake clock). Runs after the application's own registrations.</summary>
    protected virtual void ConfigureTestServices(IServiceCollection services)
    {
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        ConfigureSettings();
        builder.UseEnvironment("Testing");
        builder.ConfigureServices(ConfigureTestServices);
        builder.UseSetting("ConnectionStrings:Default", _connectionString);
        builder.UseSetting("Storage:RootPath", _storageRoot);
        foreach (var (key, value) in _settings)
        {
            builder.UseSetting(key, value);
        }
    }

    public async ValueTask InitializeAsync()
    {
        // Creating the client builds the host; then create the database from the migrations.
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<JuvoDbContext>();
        await db.Database.MigrateAsync();
    }

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        await TestDatabase.DropAsync(_connectionString);
        if (Directory.Exists(_storageRoot))
        {
            Directory.Delete(_storageRoot, recursive: true);
        }

        GC.SuppressFinalize(this);
    }
}
