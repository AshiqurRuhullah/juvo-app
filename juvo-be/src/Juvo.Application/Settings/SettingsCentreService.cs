using System.Globalization;
using System.Text.Json;
using Juvo.Application.Abstractions;
using Juvo.Application.Identity;
using Juvo.Application.Ums;
using Juvo.Domain.Platform;
using Juvo.Shared.Results;
using Juvo.Shared.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Juvo.Application.Settings;

/// <summary>One setting as the settings centre shows it. A sensitive setting never carries its value or default.</summary>
public sealed record SettingDto(
    string Key, string Group, string Type, object? Value, object? Default, string Source, bool IsSet, string DescriptionEn, string DescriptionBn,
    IReadOnlyList<string>? AllowedValues, decimal? Min, decimal? Max, int? MaxLength, bool RequiresRestart, bool Sensitive, bool ReadOnly, bool CanEdit, string? EditPermission,
    string? Version, DateTimeOffset? UpdatedAt);

public sealed record SettingGroupDto(string Group, IReadOnlyList<SettingDto> Settings);

public sealed record SettingsListDto(IReadOnlyList<SettingGroupDto> Groups);

/// <summary><c>value</c> is a JSON boolean, number or string. <c>version</c> (from a list or an earlier change) makes the change fail with 409 when someone else changed the setting meanwhile.</summary>
public sealed record UpdateSettingRequest(JsonElement Value, string? Version);

public sealed record SettingChangeDto(Guid Id, string Key, string Action, string? OldValue, string? NewValue, Guid? ChangedBy, string? ChangedByName, DateTimeOffset At);

/// <summary>
/// The settings centre (FR-AF01, FR-AF08 in part): one API over the <c>PlatformSettings</c> rows that the modules already read. It never keeps state of its own; a change here
/// is the same row a module's own endpoint would write, so both stay in agreement. Values are checked against <see cref="SettingRegistry"/>, every change is audited with
/// old and new value by the generic audit (sensitive values are masked), and reads go through <see cref="ISettingsReader"/>.
/// </summary>
public sealed class SettingsCentreService(IApplicationDbContext db, ISettingsReader reader, ActorContext actors, IOptions<AuthOptions> auth)
{
    public const string AuditEntity = "PlatformSetting";
    public const string Masked = "***";

    private sealed record ReadOnlyEntry(SettingDefinition Definition, string Value, string Source);

    public async Task<Result<SettingsListDto>> ListAsync(CancellationToken cancellationToken)
    {
        if (await actors.GetAsync(cancellationToken) is not { } actor)
        {
            return Error.Unauthorized();
        }

        var snapshot = await reader.SnapshotAsync(cancellationToken);
        var items = SettingRegistry.All.Select(d => Describe(d, snapshot.GetValueOrDefault(d.Key), actor.Access.HasAnywhere(d.EditPermission)))
            .Concat(ReadOnlyEntries().Select(e => DescribeReadOnly(e)))
            .ToList();
        var groups = SettingGroups.Order.Select(g => new SettingGroupDto(g, [.. items.Where(i => i.Group == g)])).Where(g => g.Settings.Count > 0).ToList();
        return new SettingsListDto(groups);
    }

    public async Task<Result<SettingDto>> GetAsync(string key, CancellationToken cancellationToken)
    {
        if (await actors.GetAsync(cancellationToken) is not { } actor)
        {
            return Error.Unauthorized();
        }

        if (ReadOnlyEntries().FirstOrDefault(e => e.Definition.Key == key) is { } fixedEntry)
        {
            return DescribeReadOnly(fixedEntry);
        }

        if (SettingRegistry.Find(key) is not { } definition)
        {
            return Unknown(key);
        }

        return Describe(definition, (await reader.SnapshotAsync(cancellationToken)).GetValueOrDefault(key), actor.Access.HasAnywhere(definition.EditPermission));
    }

    /// <summary>Stores one value. The row is stored even when the value equals the default (an explicit choice that survives a later change of the default); <see cref="ResetAsync"/> removes it.</summary>
    public async Task<Result<SettingDto>> SetAsync(string key, UpdateSettingRequest request, CancellationToken cancellationToken)
    {
        var (actor, definition, problem) = await AuthoriseAsync(key, cancellationToken);
        if (problem is not null)
        {
            return problem;
        }

        var raw = SettingDefinition.FromJson(request.Value);
        if (raw is null)
        {
            return new Error(ErrorCodes.Settings.InvalidValue, "Send a true/false, a number or a text.", ErrorType.Validation,
                new Dictionary<string, string[]> { ["value"] = ["Send a true/false, a number or a text."] });
        }

        var normalised = definition!.Normalize(raw);
        if (normalised.IsFailure)
        {
            return normalised.Error!;
        }

        var row = await db.PlatformSettings.FirstOrDefaultAsync(s => s.Key == key, cancellationToken);
        if (request.Version is { Length: > 0 } version && (row is null || !VersionMatches(row.RowVersion, version)))
        {
            return Error.Conflict(ErrorCodes.General.ConcurrencyConflict, "Someone else changed this setting. Reload it and try again.");
        }

        if (row is null)
        {
            db.PlatformSettings.Add(new Domain.Orders.PlatformSetting(key, normalised.Value));
        }
        else
        {
            row.Set(normalised.Value);
        }

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (Exception e) when (e is DbUpdateConcurrencyException or DbUpdateException)
        {
            return Error.Conflict(ErrorCodes.General.ConcurrencyConflict, "Someone else changed this setting. Reload it and try again.");
        }

        return Describe(definition, (await reader.SnapshotAsync(cancellationToken)).GetValueOrDefault(key), actor!.Access.HasAnywhere(definition.EditPermission));
    }

    /// <summary>Removes the stored row, so the default applies again. Resetting a setting that has no row changes nothing.</summary>
    public async Task<Result<SettingDto>> ResetAsync(string key, CancellationToken cancellationToken)
    {
        var (actor, definition, problem) = await AuthoriseAsync(key, cancellationToken);
        if (problem is not null)
        {
            return problem;
        }

        var row = await db.PlatformSettings.FirstOrDefaultAsync(s => s.Key == key, cancellationToken);
        if (row is not null)
        {
            db.PlatformSettings.Remove(row);
            try
            {
                await db.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateConcurrencyException)
            {
                return Error.Conflict(ErrorCodes.General.ConcurrencyConflict, "Someone else changed this setting. Reload it and try again.");
            }
        }

        return Describe(definition!, (await reader.SnapshotAsync(cancellationToken)).GetValueOrDefault(key), actor!.Access.HasAnywhere(definition!.EditPermission));
    }

    /// <summary>The newest changes of registered settings, newest first, from the audit log. Sensitive values are masked.</summary>
    public async Task<Result<IReadOnlyList<SettingChangeDto>>> HistoryAsync(string? key, int limit, CancellationToken cancellationToken)
    {
        if (await actors.GetAsync(cancellationToken) is null)
        {
            return Error.Unauthorized();
        }

        limit = Math.Clamp(limit, 1, 200);
        var rows = await db.AuditLogs.AsNoTracking().Where(a => a.Entity == AuditEntity).OrderByDescending(a => a.OccurredAt).ThenByDescending(a => a.Id).Take(600).ToListAsync(cancellationToken);
        var ids = rows.Select(r => r.EntityId).Distinct().ToList();

        // An update only records what changed (the value), so the key comes from the row, or from the audit entry that created or deleted it.
        var guids = ids.Select(i => Guid.TryParse(i, out var g) ? g : (Guid?)null).Where(g => g.HasValue).Select(g => g!.Value).ToList();
        var keys = await db.PlatformSettings.AsNoTracking().Where(s => guids.Contains(s.Id)).ToDictionaryAsync(s => s.Id.ToString(), s => s.Key, cancellationToken);
        var snapshots = await db.AuditLogs.AsNoTracking().Where(a => a.Entity == AuditEntity && ids.Contains(a.EntityId) && (a.Action == AuditAction.Created || a.Action == AuditAction.Deleted)).ToListAsync(cancellationToken);
        foreach (var s in snapshots)
        {
            if (!keys.ContainsKey(s.EntityId) && ReadProperty(s.NewValue ?? s.OldValue, "key") is { } k)
            {
                keys[s.EntityId] = k;
            }
        }

        var list = new List<SettingChangeDto>();
        foreach (var r in rows)
        {
            if (!keys.TryGetValue(r.EntityId, out var settingKey) || SettingRegistry.Find(settingKey) is not { } definition || (key is not null && settingKey != key))
            {
                continue;
            }

            var oldValue = ReadProperty(r.OldValue, "value");
            var newValue = ReadProperty(r.NewValue, "value");
            if (r.Action == AuditAction.Updated && oldValue is null && newValue is null)
            {
                continue;
            }

            var action = r.Action == AuditAction.Deleted ? "reset" : "set";
            list.Add(new SettingChangeDto(r.Id, settingKey, action, Mask(definition, oldValue), Mask(definition, newValue), r.UserId, null, r.OccurredAt));
            if (list.Count == limit)
            {
                break;
            }
        }

        var userIds = list.Where(c => c.ChangedBy.HasValue).Select(c => c.ChangedBy!.Value).Distinct().ToList();
        var names = await db.Users.AsNoTracking().Where(u => userIds.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.FullName, cancellationToken);
        IReadOnlyList<SettingChangeDto> result = [.. list.Select(c => c with { ChangedByName = c.ChangedBy is { } id && names.TryGetValue(id, out var n) ? n : null })];
        return Result.Success(result);
    }

    // ---- helpers -----------------------------------------------------------------------------------------

    private async Task<(Actor? Actor, SettingDefinition? Definition, Error? Problem)> AuthoriseAsync(string key, CancellationToken cancellationToken)
    {
        if (await actors.GetAsync(cancellationToken) is not { } actor)
        {
            return (null, null, Error.Unauthorized());
        }

        if (ReadOnlyEntries().Any(e => e.Definition.Key == key))
        {
            return (null, null, Error.Conflict(ErrorCodes.Settings.ReadOnly, "This setting comes from the server configuration and cannot be changed here."));
        }

        if (SettingRegistry.Find(key) is not { } definition)
        {
            return (null, null, Unknown(key));
        }

        return actor.Access.HasAnywhere(definition.EditPermission) ? (actor, definition, null) : (null, null, Error.Forbidden());
    }

    private static Error Unknown(string key) => Error.NotFound(ErrorCodes.Settings.UnknownKey, $"There is no setting '{key}'.");

    private static bool VersionMatches(byte[] rowVersion, string version) => string.Equals(Convert.ToBase64String(rowVersion), version, StringComparison.Ordinal);

    private static string? Mask(SettingDefinition definition, string? value) => value is null ? null : definition.Sensitive ? Masked : value;

    private static string? ReadProperty(string? json, string name)
    {
        if (string.IsNullOrEmpty(json))
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.EnumerateObject().FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)) is { Value.ValueKind: JsonValueKind.String } hit ? hit.Value.GetString() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static SettingDto Describe(SettingDefinition d, StoredSetting? stored, bool canEdit)
    {
        // A stored text that no longer passes the definition is ignored by readers that validate, so it is reported as the default.
        var valid = stored is not null && d.Normalize(stored.Value) is { IsSuccess: true };
        var current = valid ? d.Normalize(stored!.Value).Value : d.Default;
        return new SettingDto(
            d.Key, d.Group, d.TypeName, d.Sensitive ? null : d.Typed(current), d.Sensitive ? null : d.Typed(d.Default), valid ? "db" : "default", valid, d.DescriptionEn, d.DescriptionBn,
            d.AllowedValues, d.Min, d.Max, d.Type == SettingType.Text ? d.MaxLength : null, d.RequiresRestart, d.Sensitive, false, canEdit, d.EditPermission,
            stored is null ? null : Convert.ToBase64String(stored.RowVersion), stored?.UpdatedAt);
    }

    private static SettingDto DescribeReadOnly(ReadOnlyEntry e)
    {
        var d = e.Definition;
        return new SettingDto(d.Key, d.Group, d.TypeName, d.Typed(e.Value), d.Typed(e.Value), e.Source, false, d.DescriptionEn, d.DescriptionBn, null, null, null, null, true, false, true, false, null, null, null);
    }

    /// <summary>Server-side values that are shown but cannot be changed at run time: the one-time-password and lock-out rules (appsettings, need a restart) and the fixed store currency (BR-005).</summary>
    private List<ReadOnlyEntry> ReadOnlyEntries()
    {
        var a = auth.Value;
        static SettingDefinition Fixed(string key, string group, SettingType type, string en, string bn) => new(key, group, type, string.Empty, en, bn, string.Empty, RequiresRestart: true);
        static string N(int v) => v.ToString(CultureInfo.InvariantCulture);
        return
        [
            new(Fixed("Auth.Otp.Length", SettingGroups.Security, SettingType.Whole, "Digits in a customer sign-in code.", "গ্রাহকের সাইন-ইন কোডের অঙ্ক সংখ্যা।"), N(a.Otp.Length), "appsettings"),
            new(Fixed("Auth.Otp.ExpiryMinutes", SettingGroups.Security, SettingType.Whole, "Minutes a sign-in code stays valid.", "সাইন-ইন কোড যত মিনিট বৈধ থাকে।"), N(a.Otp.ExpiryMinutes), "appsettings"),
            new(Fixed("Auth.Otp.ResendCooldownSeconds", SettingGroups.Security, SettingType.Whole, "Seconds before another code may be requested.", "আরেকটি কোড চাওয়ার আগে অপেক্ষার সেকেন্ড।"), N(a.Otp.ResendCooldownSeconds), "appsettings"),
            new(Fixed("Auth.Otp.MaxRequestsPerHourPerMobile", SettingGroups.Security, SettingType.Whole, "Most codes one mobile number may request per hour.", "একটি মোবাইল নম্বর ঘণ্টায় সর্বোচ্চ যত কোড চাইতে পারে।"), N(a.Otp.MaxRequestsPerHourPerMobile), "appsettings"),
            new(Fixed("Auth.MaxFailedAccessAttempts", SettingGroups.Security, SettingType.Whole, "Wrong staff passwords before the account locks.", "অ্যাকাউন্ট লক হওয়ার আগে ভুল পাসওয়ার্ডের সংখ্যা।"), N(a.MaxFailedAccessAttempts), "appsettings"),
            new(Fixed("Auth.LockoutMinutes", SettingGroups.Security, SettingType.Whole, "Minutes a locked staff account stays locked.", "লক হওয়া অ্যাকাউন্ট যত মিনিট লক থাকে।"), N(a.LockoutMinutes), "appsettings"),
            new(Fixed("Auth.RequireTwoFactorForSuperAdmin", SettingGroups.Security, SettingType.Bool, "Super Admin must use two-step sign-in.", "সুপার অ্যাডমিনকে দ্বি-স্তর সাইন-ইন ব্যবহার করতে হবে।"), a.RequireTwoFactorForSuperAdmin ? "true" : "false", "appsettings"),
            new(Fixed("Currency.Store", SettingGroups.Locale, SettingType.Text, "Currency prices are stored in (fixed by BR-005; international display is a conversion).", "যে মুদ্রায় দাম সংরক্ষিত হয় (BR-005 অনুযায়ী স্থির; আন্তর্জাতিক প্রদর্শন রূপান্তর মাত্র)।"), "BDT", "default"),
        ];
    }
}
