using System.Globalization;
using Juvo.Application.Abstractions;
using Juvo.Application.Orders;
using Juvo.Domain.Branches;
using Juvo.Domain.Support;
using Juvo.Domain.Billing;
using Juvo.Shared.Results;
using Microsoft.EntityFrameworkCore;

namespace Juvo.Application.Operations;

/// <summary>How a goods receipt updates the product cost price (setting <c>Purchasing.CostMethod</c>).</summary>
public enum CostMethod
{
    /// <summary>The cost becomes the unit cost of the newest receipt.</summary>
    LastCost = 1,

    /// <summary>The cost becomes the average of what was on hand (at the old cost) and what arrived (at the new cost), weighted by quantity.</summary>
    WeightedAverage = 2,
}

/// <summary>Keys of the runtime settings of purchasing, expenses and support (table <c>PlatformSettings</c>). Every key has a safe default (docs/assumptions.md section U).</summary>
public static class OpsSettingKeys
{
    public const string CostMethod = "Purchasing.CostMethod";
    public const string ExpenseApprovalThreshold = "Expenses.ApprovalThreshold";
    public const string ReopenDays = "Support.ReopenDays";
    public const string AutoCloseDays = "Support.AutoCloseDays";
    public const string MaxTicketsPerHour = "Support.MaxTicketsPerMobilePerHour";

    public static string SlaResponseHours(TicketPriority priority) => $"Support.Sla.ResponseHours.{priority}";

    public static string SlaResolutionHours(TicketPriority priority) => $"Support.Sla.ResolutionHours.{priority}";

    public static readonly IReadOnlyDictionary<TicketPriority, int> DefaultResponseHours = new Dictionary<TicketPriority, int>
    {
        [TicketPriority.Low] = 48, [TicketPriority.Normal] = 24, [TicketPriority.High] = 8, [TicketPriority.Urgent] = 2,
    };

    public static readonly IReadOnlyDictionary<TicketPriority, int> DefaultResolutionHours = new Dictionary<TicketPriority, int>
    {
        [TicketPriority.Low] = 168, [TicketPriority.Normal] = 72, [TicketPriority.High] = 24, [TicketPriority.Urgent] = 8,
    };
}

public sealed record OpsSettings(
    CostMethod CostMethod, decimal ExpenseApprovalThreshold, int ReopenDays, int AutoCloseDays, int MaxTicketsPerHour,
    IReadOnlyDictionary<TicketPriority, int> ResponseHours, IReadOnlyDictionary<TicketPriority, int> ResolutionHours)
{
    public (DateTimeOffset ResponseDue, DateTimeOffset ResolveDue) DueTimes(TicketPriority priority, DateTimeOffset from) =>
        (from.AddHours(ResponseHours[priority]), from.AddHours(ResolutionHours[priority]));
}

public sealed class OpsSettingsService(SettingsService settings)
{
    public async Task<OpsSettings> ReadAsync(CancellationToken cancellationToken)
    {
        var priorities = Enum.GetValues<TicketPriority>();
        var keys = new List<string> { OpsSettingKeys.CostMethod, OpsSettingKeys.ExpenseApprovalThreshold, OpsSettingKeys.ReopenDays, OpsSettingKeys.AutoCloseDays, OpsSettingKeys.MaxTicketsPerHour };
        keys.AddRange(priorities.SelectMany(p => new[] { OpsSettingKeys.SlaResponseHours(p), OpsSettingKeys.SlaResolutionHours(p) }));
        var values = await settings.ReadAsync(keys, cancellationToken);
        return new OpsSettings(
            SettingsService.EnumValue(values, OpsSettingKeys.CostMethod, CostMethod.LastCost),
            SettingsService.Number(values, OpsSettingKeys.ExpenseApprovalThreshold, 0m, 0m),
            (int)SettingsService.Number(values, OpsSettingKeys.ReopenDays, 7m, 0m),
            (int)SettingsService.Number(values, OpsSettingKeys.AutoCloseDays, 3m, 1m),
            (int)SettingsService.Number(values, OpsSettingKeys.MaxTicketsPerHour, 5m, 1m),
            priorities.ToDictionary(p => p, p => (int)SettingsService.Number(values, OpsSettingKeys.SlaResponseHours(p), OpsSettingKeys.DefaultResponseHours[p], 1m)),
            priorities.ToDictionary(p => p, p => (int)SettingsService.Number(values, OpsSettingKeys.SlaResolutionHours(p), OpsSettingKeys.DefaultResolutionHours[p], 1m)));
    }
}

/// <summary>
/// Gap-free document numbers for purchasing, expenses and support: <c>PO-DHK01-2611-000001</c> per branch, kind and month, and <c>TK-2611-000001</c> for numbers that belong to no branch.
/// The counter row is incremented inside the caller's transaction under an application lock, so a rolled-back document gives its number back and concurrent issuers wait: numbers
/// never skip and never repeat.
/// </summary>
public sealed class NumberAllocator(IApplicationDbContext db)
{
    public async Task<string> NextForBranchAsync(Branch branch, string kind, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var period = OrderPlacement.LocalTime(branch, now).ToString("yyMM", CultureInfo.InvariantCulture);
        var branchId = branch.Id;
        await db.AcquireTransactionLockAsync($"docseq:{branchId:N}:{kind}:{period}", cancellationToken);
        var bumped = await db.DocumentSequences.Where(s => s.BranchId == branchId && s.Kind == kind && s.Period == period)
            .ExecuteUpdateAsync(set => set.SetProperty(s => s.LastNumber, s => s.LastNumber + 1), cancellationToken);
        if (bumped == 0)
        {
            var row = new DocumentSequence(branchId, kind, period, 0);
            db.DocumentSequences.Add(row);
            try
            {
                await db.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateException)
            {
                db.DocumentSequences.Remove(row);
            }

            await db.DocumentSequences.Where(s => s.BranchId == branchId && s.Kind == kind && s.Period == period)
                .ExecuteUpdateAsync(set => set.SetProperty(s => s.LastNumber, s => s.LastNumber + 1), cancellationToken);
        }

        var next = await db.DocumentSequences.AsNoTracking().Where(s => s.BranchId == branchId && s.Kind == kind && s.Period == period).Select(s => s.LastNumber).FirstAsync(cancellationToken);
        return $"{kind}-{branch.OrderPrefix}-{period}-{next:D6}";
    }

    public async Task<string> NextGlobalAsync(string kind, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var period = now.ToOffset(TimeSpan.FromHours(6)).ToString("yyMM", CultureInfo.InvariantCulture);
        await db.AcquireTransactionLockAsync($"globalseq:{kind}:{period}", cancellationToken);
        var bumped = await db.GlobalSequences.Where(s => s.Kind == kind && s.Period == period)
            .ExecuteUpdateAsync(set => set.SetProperty(s => s.LastNumber, s => s.LastNumber + 1), cancellationToken);
        if (bumped == 0)
        {
            var row = new GlobalSequence(kind, period, 0);
            db.GlobalSequences.Add(row);
            try
            {
                await db.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateException)
            {
                db.GlobalSequences.Remove(row);
            }

            await db.GlobalSequences.Where(s => s.Kind == kind && s.Period == period)
                .ExecuteUpdateAsync(set => set.SetProperty(s => s.LastNumber, s => s.LastNumber + 1), cancellationToken);
        }

        var next = await db.GlobalSequences.AsNoTracking().Where(s => s.Kind == kind && s.Period == period).Select(s => s.LastNumber).FirstAsync(cancellationToken);
        return $"{kind}-{period}-{next:D6}";
    }
}

public sealed record StoredFile(string Path, string FileName, string ContentType, long Size, string Url);

/// <summary>
/// Saves and re-reads attachments (receipts, supplier documents, ticket pictures) through <see cref="IFileStorage"/>. Only JPEG, PNG, WebP and PDF files up to 8 MB are accepted, judged by
/// the first bytes of the file and not by its name. A path sent back by a client is only accepted when it lies under the folder and the file exists.
/// </summary>
public sealed class AttachmentStore(IFileStorage storage, TimeProvider clock)
{
    public const long MaxBytes = 8 * 1024 * 1024;

    private static readonly Dictionary<string, string> Types = new(StringComparer.OrdinalIgnoreCase)
    {
        [".jpg"] = "image/jpeg", [".jpeg"] = "image/jpeg", [".png"] = "image/png", [".webp"] = "image/webp", [".pdf"] = "application/pdf",
    };

    private static Error Invalid(string code, string message) => Error.Failure(code, message);

    public async Task<Result<StoredFile>> SaveAsync(string folder, Stream content, string? fileName, string invalidCode, CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        await content.CopyToAsync(buffer, cancellationToken);
        if (buffer.Length is 0 or > MaxBytes)
        {
            return Error.Validation(new Dictionary<string, string[]> { ["file"] = ["The file must be between 1 byte and 8 MB."] });
        }

        var bytes = buffer.GetBuffer().AsSpan(0, (int)buffer.Length);
        var extension = Sniff(bytes);
        if (extension is null)
        {
            return Error.Validation(new Dictionary<string, string[]> { ["file"] = ["Send a JPEG, PNG, WebP or PDF file."] });
        }

        var cleanName = CleanName(fileName, extension);
        var path = $"{folder}/{clock.GetUtcNow():yyMM}/{Guid.NewGuid():N}-{cleanName}";
        buffer.Position = 0;
        await storage.SaveAsync(path, buffer, Types[extension], cancellationToken);
        return new StoredFile(path, cleanName, Types[extension], buffer.Length, storage.GetPublicUrl(path));
    }

    /// <summary>Checks paths that came back from a client and rebuilds their file facts. Fails with <paramref name="invalidCode"/> for anything unknown.</summary>
    public async Task<Result<IReadOnlyList<StoredFile>>> ResolveAsync(string folder, IReadOnlyList<string>? paths, string invalidCode, int max, CancellationToken cancellationToken)
    {
        var result = new List<StoredFile>();
        foreach (var path in (paths ?? []).Where(p => !string.IsNullOrWhiteSpace(p)).Distinct(StringComparer.Ordinal))
        {
            if (result.Count >= max)
            {
                return Invalid(invalidCode, $"At most {max} files can be attached.");
            }

            if (!path.StartsWith(folder + "/", StringComparison.Ordinal) || path.Contains("..", StringComparison.Ordinal) || !await storage.ExistsAsync(path, cancellationToken))
            {
                return Invalid(invalidCode, "An attached file was not uploaded here or no longer exists.");
            }

            var name = System.IO.Path.GetFileName(path);
            var dash = name.IndexOf('-', StringComparison.Ordinal);
            var display = dash > 0 ? name[(dash + 1)..] : name;
            var type = Types.GetValueOrDefault(System.IO.Path.GetExtension(path), "application/octet-stream");
            long size = 0;
            await using (var stream = await storage.OpenReadAsync(path, cancellationToken))
            {
                size = stream?.Length ?? 0;
            }

            result.Add(new StoredFile(path, display, type, size, storage.GetPublicUrl(path)));
        }

        return result;
    }

    public string UrlOf(string path) => storage.GetPublicUrl(path);

    private static string? Sniff(ReadOnlySpan<byte> b)
    {
        if (b.Length >= 3 && b[0] == 0xFF && b[1] == 0xD8 && b[2] == 0xFF)
        {
            return ".jpg";
        }

        if (b.Length >= 8 && b[0] == 0x89 && b[1] == 0x50 && b[2] == 0x4E && b[3] == 0x47)
        {
            return ".png";
        }

        if (b.Length >= 12 && b[0] == 'R' && b[1] == 'I' && b[2] == 'F' && b[3] == 'F' && b[8] == 'W' && b[9] == 'E' && b[10] == 'B' && b[11] == 'P')
        {
            return ".webp";
        }

        if (b.Length >= 5 && b[0] == '%' && b[1] == 'P' && b[2] == 'D' && b[3] == 'F' && b[4] == '-')
        {
            return ".pdf";
        }

        return null;
    }

    private static string CleanName(string? fileName, string extension)
    {
        var stem = System.IO.Path.GetFileNameWithoutExtension(fileName ?? string.Empty);
        var safe = new string([.. stem.Where(c => char.IsLetterOrDigit(c) || c is '_' or '.').Take(60)]).Trim('.');
        return (safe.Length == 0 ? "file" : safe) + extension;
    }
}
