using Juvo.Api.Configuration;
using Juvo.Application;
using Juvo.Infrastructure;
using Juvo.Infrastructure.Options;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddApplication();
builder.Services.AddInfrastructure(builder.Configuration);
builder.Services.AddJuvoApi(builder.Configuration);

var app = builder.Build();

// "dotnet run --project src/Juvo.Api -- --migrate" applies migrations and exits (Production procedure).
if (args.Contains("--migrate"))
{
    await app.MigrateDatabaseAsync();
    return;
}

if (app.Environment.IsDevelopment() && app.Services.GetRequiredService<IOptions<DatabaseOptions>>().Value.AutoMigrate)
{
    await app.MigrateDatabaseAsync();
}

app.UseJuvoApi();
app.MapJuvoEndpoints();

await app.RunAsync();

/// <summary>Entry point type, public so integration tests can host the API in memory.</summary>
public partial class Program;
