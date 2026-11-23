using Juvo.Api.Configuration;
using Juvo.Api.Http;
using Juvo.Application.Settings;
using Juvo.Shared.Security;

namespace Juvo.Api.Endpoints;

/// <summary>
/// The settings centre (FR-AF01): every runtime setting in one place, over the same <c>PlatformSettings</c> rows the modules read. Listing and history need
/// <c>settings.system.view</c>; changing or resetting a setting also needs the permission named by that setting (<c>settings.system.edit</c>, <c>settings.feature.edit</c>
/// or <c>payments.gateway.edit</c>), checked inside. Settings are global, so the permissions are not branch-scoped.
/// </summary>
internal static class SettingsEndpoints
{
    public static WebApplication MapSettingsEndpoints(this WebApplication app)
    {
        var g = app.MapApiV1().MapGroup("/settings").WithTags("Settings");

        g.MapGet("", async (SettingsCentreService s, CancellationToken ct) => (await s.ListAsync(ct)).ToHttpResult())
            .RequirePermission(Permissions.Settings.SystemView).Produces<SettingsListDto>()
            .WithSummary("Every runtime setting, grouped: current value, default, source (db, default or appsettings), limits, the permission needed to change it and whether the caller may. Sensitive values are never returned.");

        g.MapGet("/history", async (string? key, int? limit, SettingsCentreService s, CancellationToken ct) => (await s.HistoryAsync(key, limit ?? 50, ct)).ToHttpResult())
            .RequirePermission(Permissions.Settings.SystemView).Produces<IReadOnlyList<SettingChangeDto>>()
            .WithSummary("Recent changes of settings from the audit log (who, when, old and new value; sensitive values masked). Optional key filter, limit up to 200.");

        g.MapGet("/{key}", async (string key, SettingsCentreService s, CancellationToken ct) => (await s.GetAsync(key, ct)).ToHttpResult())
            .RequirePermission(Permissions.Settings.SystemView).Produces<SettingDto>().WithSummary("One setting.");

        g.MapPut("/{key}", async (string key, UpdateSettingRequest r, SettingsCentreService s, CancellationToken ct) => (await s.SetAsync(key, r, ct)).ToHttpResult())
            .RequirePermission(Permissions.Settings.SystemView).Produces<SettingDto>()
            .WithSummary("Change one setting. The value is checked against the setting's type and limits. Send the version from the list to fail with 409 when someone else changed it meanwhile.");

        g.MapDelete("/{key}", async (string key, SettingsCentreService s, CancellationToken ct) => (await s.ResetAsync(key, ct)).ToHttpResult())
            .RequirePermission(Permissions.Settings.SystemView).Produces<SettingDto>().WithSummary("Reset one setting to its default (removes the stored value).");

        return app;
    }
}
