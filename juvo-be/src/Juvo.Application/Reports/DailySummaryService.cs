using System.Globalization;
using System.Text;
using Juvo.Application.Abstractions;
using Juvo.Application.Operations;
using Juvo.Application.Orders;
using Microsoft.EntityFrameworkCore;

namespace Juvo.Application.Reports;

/// <summary>Keys of the daily summary email (table <c>PlatformSettings</c>, docs/reports-api.md). Without an owner email the summary is off.</summary>
public static class DailySummarySettings
{
    public const string Email = "Reports.DailySummary.Email";
    public const string Hour = "Reports.DailySummary.Hour";
    public const string LastSent = "Reports.DailySummary.LastSentDate";
    public const string Template = "daily-summary";
    public const int DefaultHour = 7;
}

public enum DailySummaryOutcome
{
    Disabled,
    NotYetDue,
    AlreadySent,
    Sent,
}

public sealed record DailySummary(DateOnly Date, int Orders, decimal Sales, decimal CashIn, decimal CashOut, int LowStock);

/// <summary>
/// FR-P07: the owner's morning email with yesterday's orders, sales, cash movement and low stock count, for every branch together. It is sent once per day: the date of the last
/// summary is stored, so a restart or a repeated run in the same day sends nothing more.
/// </summary>
public sealed class DailySummaryService(IApplicationDbContext db, ReportData data, SettingsService settings, IEmailSender email, TimeProvider clock)
{
    public async Task<DailySummary> BuildAsync(DateOnly date, CancellationToken ct)
    {
        var s = ReportScope.System(date, date, clock.GetUtcNow());
        var orders = await data.Orders(s).GroupBy(o => 1).Select(g => new { Count = g.Count(), Sales = g.Sum(o => o.GrandTotal) }).FirstOrDefaultAsync(ct);
        var cash = await data.CashReceivedAsync(s, ct);
        var low = await db.StockLevels.AsNoTracking().CountAsync(l => l.ReorderLevel > 0 && l.OnHand - l.Reserved <= l.ReorderLevel, ct);
        return new DailySummary(date, orders?.Count ?? 0, ReportMath.Round2(orders?.Sales ?? 0m), ReportMath.Round2(cash.Receipts + cash.CodSettled), ReportMath.Round2(cash.Refunds), low);
    }

    public async Task<DailySummaryOutcome> SendIfDueAsync(CancellationToken ct)
    {
        var values = await settings.ReadAsync([DailySummarySettings.Email, DailySummarySettings.Hour, DailySummarySettings.LastSent], ct);
        var to = values.GetValueOrDefault(DailySummarySettings.Email)?.Trim();
        if (string.IsNullOrEmpty(to))
        {
            return DailySummaryOutcome.Disabled;
        }

        var hour = (int)SettingsService.Number(values, DailySummarySettings.Hour, DailySummarySettings.DefaultHour, 0m);
        var local = clock.GetUtcNow().ToOffset(Ops.Bst);
        if (hour is < 0 or > 23 || local.Hour < hour)
        {
            return DailySummaryOutcome.NotYetDue;
        }

        var yesterday = DateOnly.FromDateTime(local.DateTime).AddDays(-1);
        var key = yesterday.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        if (values.GetValueOrDefault(DailySummarySettings.LastSent) == key)
        {
            return DailySummaryOutcome.AlreadySent;
        }

        var summary = await BuildAsync(yesterday, ct);
        var body = new StringBuilder()
            .AppendLine(CultureInfo.InvariantCulture, $"Juvo BD daily summary for {key}")
            .AppendLine(CultureInfo.InvariantCulture, $"Orders: {summary.Orders}")
            .AppendLine(CultureInfo.InvariantCulture, $"Sales (BDT): {summary.Sales:0.00}")
            .AppendLine(CultureInfo.InvariantCulture, $"Cash in (customer payments and settled COD, BDT): {summary.CashIn:0.00}")
            .AppendLine(CultureInfo.InvariantCulture, $"Refunds paid (BDT): {summary.CashOut:0.00}")
            .AppendLine(CultureInfo.InvariantCulture, $"Low stock items: {summary.LowStock}")
            .ToString();
        await email.SendAsync(to, DailySummarySettings.Template, $"Juvo BD daily summary {key}", body, ct);
        await settings.SetAsync(DailySummarySettings.LastSent, key, ct);
        await db.SaveChangesAsync(ct);
        return DailySummaryOutcome.Sent;
    }
}
