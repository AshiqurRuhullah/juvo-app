using Juvo.Application.Abstractions;
using Juvo.Domain.Marketing;
using Juvo.Domain.Notifications;
using Juvo.Shared.Results;
using Microsoft.EntityFrameworkCore;

namespace Juvo.Application.Marketing;

/// <summary>Creates the four automations, switched off, with their starting texts. Safe to call often and from two places at once.</summary>
public sealed class AutomationSeeder(IApplicationDbContext db)
{
    public async Task EnsureSeededAsync(CancellationToken cancellationToken)
    {
        var present = await db.Automations.AsNoTracking().Select(a => a.Kind).ToListAsync(cancellationToken);
        var missing = AutomationCatalog.All.Where(d => !present.Contains(d.Kind)).ToList();
        if (missing.Count == 0)
        {
            return;
        }

        foreach (var d in missing)
        {
            db.Automations.Add(new Automation(d.Kind, NotificationChannel.Sms, d.DelayMinutes, d.WindowDays, d.CapDays, d.SubjectEn, d.SubjectBn, d.BodyEn, d.BodyBn));
        }

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            db.DiscardUnsavedAdditions(); // another request seeded them first
        }
    }
}

/// <summary>Switches, delays, texts and caps of the automations, and their counters (module AC, FR-AC03).</summary>
public sealed class AutomationsService(IApplicationDbContext db, AutomationSeeder seeder, AutomationEngine engine, MarketingSettingsService settingsService, AttributionService attribution)
{
    private static Error NotFound() => Error.NotFound(ErrorCodes.Marketing.AutomationNotFound, "Automation not found.");

    private static Error Invalid(string field, string message) => new(ErrorCodes.Marketing.AutomationInvalid, "The automation is not valid.", ErrorType.Validation, new Dictionary<string, string[]> { [field] = [message] });

    private static AutomationDto ToDto(Automation a)
    {
        var d = AutomationCatalog.Of(a.Kind);
        return new AutomationDto(a.Id, a.Kind, d.Name, d.Description, a.IsEnabled, a.Channel, a.DelayMinutes, a.WindowDays, a.CapDays, a.SubjectEn, a.SubjectBn, a.BodyEn, a.BodyBn, d.Placeholders, a.LastRunAt);
    }

    public async Task<Result<IReadOnlyList<AutomationDto>>> ListAsync(CancellationToken cancellationToken)
    {
        await seeder.EnsureSeededAsync(cancellationToken);
        var rows = await db.Automations.AsNoTracking().OrderBy(a => a.Kind).ToListAsync(cancellationToken);
        return rows.Select(ToDto).ToList();
    }

    public async Task<Result<AutomationDto>> GetAsync(AutomationKind kind, CancellationToken cancellationToken)
    {
        await seeder.EnsureSeededAsync(cancellationToken);
        var row = await db.Automations.AsNoTracking().FirstOrDefaultAsync(a => a.Kind == kind, cancellationToken);
        return row is null ? NotFound() : ToDto(row);
    }

    public async Task<Result<AutomationDto>> UpdateAsync(AutomationKind kind, UpdateAutomationRequest r, CancellationToken cancellationToken)
    {
        await seeder.EnsureSeededAsync(cancellationToken);
        var row = await db.Automations.FirstOrDefaultAsync(a => a.Kind == kind, cancellationToken);
        if (row is null)
        {
            return NotFound();
        }

        var def = AutomationCatalog.Of(kind);
        var channel = r.Channel ?? row.Channel;
        var delay = r.DelayMinutes ?? row.DelayMinutes;
        var window = r.WindowDays ?? row.WindowDays;
        var cap = r.CapDays ?? row.CapDays;
        var subjectEn = r.SubjectEn ?? row.SubjectEn;
        var subjectBn = r.SubjectBn ?? row.SubjectBn;
        var bodyEn = r.BodyEn ?? row.BodyEn;
        var bodyBn = r.BodyBn ?? row.BodyBn;
        if (channel is not (NotificationChannel.Sms or NotificationChannel.Email))
        {
            return Invalid("channel", "An automation goes out by Sms or Email.");
        }

        if (delay is < 0 or > 525_600)
        {
            return Invalid("delayMinutes", "Use 0 to 525600 minutes (one year).");
        }

        if (window is < 1 or > 365)
        {
            return Invalid("windowDays", "Use 1 to 365 days.");
        }

        if (cap is < 0 or > 365)
        {
            return Invalid("capDays", "Use 0 (no cap) to 365 days.");
        }

        var max = channel == NotificationChannel.Sms ? 1000 : 20000;
        if (string.IsNullOrWhiteSpace(bodyEn) || bodyEn.Length > max)
        {
            return Invalid("bodyEn", $"Write the English message (up to {max} characters).");
        }

        if (string.IsNullOrWhiteSpace(bodyBn) || bodyBn.Length > max)
        {
            return Invalid("bodyBn", $"Write the Bengali message (up to {max} characters).");
        }

        if (channel == NotificationChannel.Email && (string.IsNullOrWhiteSpace(subjectEn) || subjectEn.Length > 200))
        {
            return Invalid("subjectEn", "An email needs an English subject (up to 200 characters).");
        }

        foreach (var (field, text) in new[] { ("bodyEn", bodyEn), ("bodyBn", bodyBn), ("subjectEn", subjectEn), ("subjectBn", subjectBn) })
        {
            if (MarketingTemplate.UnknownIn(text, def.Placeholders) is { Count: > 0 } unknown)
            {
                return Invalid(field, $"Unknown placeholder {{{unknown[0]}}}. Use {string.Join(", ", def.Placeholders.Select(p => "{" + p + "}"))}.");
            }
        }

        row.Update(r.IsEnabled ?? row.IsEnabled, channel, delay, window, cap, subjectEn, subjectBn, bodyEn.Trim(), bodyBn.Trim());
        await db.SaveChangesAsync(cancellationToken);
        return ToDto(row);
    }

    public async Task<Result<AutomationDryRunDto>> DryRunAsync(CancellationToken cancellationToken) => await engine.DryRunAsync(cancellationToken);

    public async Task<Result<AutomationResultsDto>> ResultsAsync(AutomationKind kind, CancellationToken cancellationToken)
    {
        await seeder.EnsureSeededAsync(cancellationToken);
        var row = await db.Automations.AsNoTracking().FirstOrDefaultAsync(a => a.Kind == kind, cancellationToken);
        if (row is null)
        {
            return NotFound();
        }

        var def = AutomationCatalog.Of(kind);
        var logs = db.NotificationLogs.AsNoTracking().Where(l => l.Template == def.TemplateCode);
        var byStatus = await logs.GroupBy(l => l.Status).Select(g => new { Status = g.Key, N = g.Count() }).ToListAsync(cancellationToken);
        int Of(params NotificationStatus[] statuses) => byStatus.Where(x => statuses.Contains(x.Status)).Sum(x => x.N);
        var window = (await settingsService.ReadAsync(cancellationToken)).AttributionWindowDays;
        var attributed = await attribution.ForLogsAsync(logs, window, cancellationToken);
        return new AutomationResultsDto(kind, def.Name, row.IsEnabled, Of(NotificationStatus.Pending, NotificationStatus.Failed, NotificationStatus.Sent, NotificationStatus.DeadLetter), Of(NotificationStatus.Sent),
            Of(NotificationStatus.Failed, NotificationStatus.DeadLetter), Of(NotificationStatus.Skipped), attributed, row.LastRunAt);
    }
}
