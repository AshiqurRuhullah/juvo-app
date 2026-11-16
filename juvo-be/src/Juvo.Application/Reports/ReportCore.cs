using System.Globalization;
using Juvo.Application.Abstractions;
using Juvo.Application.Inventory;
using Juvo.Application.Operations;
using Juvo.Application.Ums;
using Juvo.Domain.Common;
using Juvo.Domain.Orders;
using Juvo.Shared.Results;
using Juvo.Shared.Security;
using Microsoft.EntityFrameworkCore;

namespace Juvo.Application.Reports;

/// <summary>The filters every report understands. Dates are Bangladesh (Asia/Dhaka) calendar days, both ends included.</summary>
public sealed record ReportQuery(DateOnly? From, DateOnly? To, Guid? BranchId, string? GroupBy = null, int? Limit = null, int? Days = null);

/// <summary>What the report was really run with (defaults filled in), echoed back so a screen or a printout can show it.</summary>
public sealed record ReportFiltersDto(DateOnly From, DateOnly To, Guid? BranchId, bool Consolidated, IReadOnlyList<string> BranchCodes, string? GroupBy, int Limit, int Days);

/// <summary>Resolved scope of one report run: the period, the branches the actor may see, and whether cost may be shown.</summary>
public sealed class ReportScope
{
    public DateOnly From { get; init; }

    public DateOnly To { get; init; }

    public DateTimeOffset FromAt { get; init; }

    /// <summary>Exclusive end (the start of the day after <see cref="To"/>).</summary>
    public DateTimeOffset ToAt { get; init; }

    /// <summary>Branches included; null means every branch.</summary>
    public Guid[]? Branches { get; init; }

    public bool CanSeeCost { get; init; }

    public string GroupBy { get; init; } = "day";

    public int Limit { get; init; } = 50;

    public int Days { get; init; } = 60;

    public Guid? BranchId { get; init; }

    public IReadOnlyList<string> BranchCodes { get; init; } = [];

    public DateTimeOffset Now { get; init; }

    public IQueryable<T> Apply<T>(IQueryable<T> query) where T : IBranchScoped
    {
        var branches = Branches;
        return branches is null ? query : query.Where(x => branches.Contains(x.BranchId));
    }

    public ReportFiltersDto Echo() => new(From, To, BranchId, BranchId is null, BranchCodes, GroupBy, Limit, Days);

    /// <summary>A scope for jobs that have no signed-in person (the daily summary): every branch, cost visible.</summary>
    public static ReportScope System(DateOnly from, DateOnly to, DateTimeOffset now) =>
        new() { From = from, To = to, FromAt = Ops.StartOfDay(from), ToAt = Ops.StartOfDay(to).AddDays(1), Branches = null, CanSeeCost = true, Now = now };
}

/// <summary>Pure helpers: Bangladesh-time buckets, rates and rounding.</summary>
public static class ReportMath
{
    public static decimal Round2(decimal value) => Math.Round(value, 2, MidpointRounding.AwayFromZero);

    /// <summary>part / whole as a percentage with two decimals (0 when whole is 0).</summary>
    public static decimal Percent(decimal part, decimal whole) => whole == 0m ? 0m : Round2(part * 100m / whole);

    /// <summary>The first day of the bucket that holds <paramref name="day"/>: the day itself, the Monday of its week, or the first of its month.</summary>
    public static DateOnly BucketStart(DateOnly day, string groupBy) => groupBy switch
    {
        "week" => day.AddDays(-(((int)day.DayOfWeek + 6) % 7)),
        "month" => new DateOnly(day.Year, day.Month, 1),
        _ => day,
    };

    public static string BucketLabel(DateOnly start, string groupBy) =>
        groupBy == "month" ? start.ToString("yyyy-MM", CultureInfo.InvariantCulture) : start.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    /// <summary>Every bucket start between two days, including empty ones, in order.</summary>
    public static List<DateOnly> Buckets(DateOnly from, DateOnly to, string groupBy)
    {
        var list = new List<DateOnly>();
        for (var b = BucketStart(from, groupBy); b <= to; b = groupBy switch { "week" => b.AddDays(7), "month" => b.AddMonths(1), _ => b.AddDays(1) })
        {
            list.Add(b);
        }

        return list;
    }

    /// <summary>The day for a SQL group key: days since 1970-01-01 of the Bangladesh local time, (unix seconds + 6 hours) / 86400.</summary>
    public static DateOnly DayFromKey(int key) => DateOnly.FromDayNumber(UnixEpochDayNumber + key);

    private const int UnixEpochDayNumber = 719162;

    public static bool IsPeriod(string? groupBy) => groupBy is "day" or "week" or "month";
}

/// <summary>Resolves and validates the scope of a report run (period, branches, cost visibility).</summary>
public sealed class ReportAccess(InventoryAccess access, IApplicationDbContext db, TimeProvider clock)
{
    public const int MaxDaysForDayGrouping = 366;
    public const int MaxDays = 1830;

    /// <summary>
    /// <paramref name="permission"/> decides which branches are visible (branch-scoped staff only get theirs, global staff get all or the chosen one).
    /// <paramref name="groupBys"/> lists the allowed groupBy values (first is the default); null means the report has no groupBy.
    /// </summary>
    public async Task<Result<ReportScope>> ScopeAsync(ReportQuery q, string permission, IReadOnlyList<string>? groupBys, bool needsRange, CancellationToken cancellationToken)
    {
        var actor = await access.ActorAsync(cancellationToken);
        if (actor.IsFailure)
        {
            return actor.Error!;
        }

        var now = clock.GetUtcNow();
        var today = Ops.BstDate(now);
        var to = q.To ?? today;
        var from = q.From ?? to.AddDays(-29);
        if (from > to)
        {
            return Error.Failure(ErrorCodes.Report.RangeInvalid, "The start date must not be after the end date.");
        }

        var groupBy = "day";
        if (groupBys is not null)
        {
            groupBy = string.IsNullOrWhiteSpace(q.GroupBy) ? groupBys[0] : q.GroupBy.Trim().ToLowerInvariant();
            if (!groupBys.Contains(groupBy))
            {
                return Ops.Invalid("groupBy", "groupBy must be one of: " + string.Join(", ", groupBys) + ".");
            }
        }

        var span = to.DayNumber - from.DayNumber + 1;
        if (needsRange && (span > MaxDays || (groupBy == "day" && span > MaxDaysForDayGrouping)))
        {
            return Error.Failure(ErrorCodes.Report.RangeTooLarge, groupBy == "day" && span <= MaxDays
                ? $"Daily grouping covers at most {MaxDaysForDayGrouping} days; choose week or month grouping or a shorter period."
                : $"A report covers at most {MaxDays} days.");
        }

        var visible = InventoryAccess.VisibleBranches(actor.Value, permission);
        Guid[]? branches;
        if (visible is null)
        {
            branches = q.BranchId is { } one ? [one] : null;
        }
        else
        {
            branches = q.BranchId is { } want ? (visible.Contains(want) ? [want] : []) : [.. visible];
        }

        var costOk = visible is null
            ? actor.Value.Access.HasAnywhere(Permissions.Catalog.ProductViewCost)
            : visible.Count > 0 && visible.All(b => actor.Value.Can(Permissions.Catalog.ProductViewCost, b));

        var codes = branches is null
            ? new List<string>()
            : await db.Branches.AsNoTracking().Where(b => branches.Contains(b.Id)).OrderBy(b => b.Code).Select(b => b.Code).ToListAsync(cancellationToken);

        return new ReportScope
        {
            From = from, To = to, FromAt = Ops.StartOfDay(from), ToAt = Ops.StartOfDay(to).AddDays(1), Branches = branches, CanSeeCost = costOk, GroupBy = groupBy,
            Limit = Math.Clamp(q.Limit ?? 50, 1, 500), Days = Math.Clamp(q.Days ?? 60, 1, 3650), BranchId = q.BranchId, BranchCodes = codes, Now = now,
        };
    }
}

/// <summary>One table of a report, ready to be written as CSV, Excel or PDF.</summary>
public sealed record ReportSection(string Key, string Title, IReadOnlyList<string> Headers, IReadOnlyList<IReadOnlyList<string?>> Rows, IReadOnlyList<string?>? Totals, IReadOnlySet<string>? NumericColumns);

/// <summary>A whole printable report: title, the filters it was run with, notes, and its tables.</summary>
public sealed record ReportDoc(string Report, string Title, IReadOnlyList<KeyValuePair<string, string>> Filters, DateTimeOffset GeneratedAt, IReadOnlyList<ReportSection> Sections, IReadOnlyList<string> Notes);

/// <summary>Plain-text builders shared by the report services when they turn rows into tables.</summary>
internal static class Cells
{
    public static string M(decimal v) => v.ToString("0.00", CultureInfo.InvariantCulture);

    public static string M(decimal? v) => v is null ? string.Empty : M(v.Value);

    public static string N(long v) => v.ToString(CultureInfo.InvariantCulture);

    public static string D(DateOnly d) => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    public static string P(decimal v) => v.ToString("0.00", CultureInfo.InvariantCulture);

    public static IReadOnlySet<string> Set(params string[] headers) => new HashSet<string>(headers);
}

/// <summary>English and Bengali texts of the report files. The key is the English text; a missing Bengali entry falls back to English. First-pass Bengali: needs native review (Step 12).</summary>
public static class ReportText
{
    private static readonly Dictionary<string, string> Bn = new()
    {
        ["Sales report"] = "বিক্রয় প্রতিবেদন", ["Order funnel report"] = "অর্ডার ফানেল প্রতিবেদন", ["Inventory report"] = "ইনভেন্টরি প্রতিবেদন", ["Finance report"] = "আর্থিক প্রতিবেদন",
        ["Courier performance report"] = "কুরিয়ার পারফরম্যান্স প্রতিবেদন", ["Customers report"] = "গ্রাহক প্রতিবেদন", ["Profit and loss"] = "লাভ-ক্ষতি",
        ["Period"] = "সময়কাল", ["Branch"] = "শাখা", ["All branches"] = "সব শাখা", ["Group by"] = "গ্রুপ", ["Generated"] = "তৈরি", ["Total"] = "মোট",
        ["Key"] = "কী", ["Label"] = "বিবরণ", ["Orders"] = "অর্ডার", ["Units"] = "ইউনিট", ["Gross"] = "মোট বিক্রয়", ["Discount"] = "ছাড়", ["Net"] = "নিট", ["Delivery"] = "ডেলিভারি", ["Grand total"] = "সর্বমোট",
        ["Stage"] = "ধাপ", ["Count"] = "সংখ্যা", ["Rate %"] = "হার %", ["Reason"] = "কারণ", ["Share %"] = "অংশ %", ["Fault"] = "দায়ী",
        ["Funnel"] = "ফানেল", ["Cancellation reasons"] = "বাতিলের কারণ", ["Return reasons"] = "ফেরতের কারণ",
        ["Summary"] = "সারসংক্ষেপ", ["By branch"] = "শাখা অনুযায়ী", ["Low stock"] = "কম স্টক", ["Stock movements"] = "স্টক চলাচল", ["Dead stock"] = "অবিক্রিত স্টক",
        ["SKU"] = "এসকেইউ", ["Product"] = "পণ্য", ["On hand"] = "মজুত", ["Reserved"] = "সংরক্ষিত", ["Available"] = "পাওয়া যাবে", ["Reorder level"] = "রিঅর্ডার লেভেল",
        ["Value at cost"] = "ক্রয়মূল্যে মূল্য", ["Value at price"] = "বিক্রয়মূল্যে মূল্য", ["Last sold"] = "সর্বশেষ বিক্রি", ["In"] = "আসা", ["Out"] = "যাওয়া", ["Movements"] = "চলাচল",
        ["Line"] = "লাইন", ["Amount"] = "পরিমাণ", ["Discounts"] = "ছাড়", ["Delivery collected"] = "ডেলিভারি চার্জ আদায়", ["Courier charges paid"] = "কুরিয়ারকে প্রদত্ত",
        ["Gateway fees (not recorded)"] = "গেটওয়ে ফি (সংরক্ষিত নয়)", ["Refunds"] = "ফেরত", ["Cost of goods"] = "পণ্যের ক্রয়মূল্য", ["Gross profit"] = "মোট লাভ", ["Net revenue"] = "নিট আয়",
        ["Net profit"] = "নিট লাভ", ["Expenses"] = "খরচ", ["Purchases received (information)"] = "ক্রয় গ্রহণ (তথ্য)", ["COD collected"] = "COD সংগৃহীত", ["COD settled"] = "COD নিষ্পত্তি",
        ["COD pending with couriers"] = "কুরিয়ারের কাছে COD বকেয়া", ["COD in transit"] = "পথে থাকা COD", ["Courier"] = "কুরিয়ার", ["Parcels"] = "পার্সেল", ["Delivered"] = "ডেলিভারি হয়েছে",
        ["Delivered %"] = "ডেলিভারি %", ["Avg delivery hours"] = "গড় ডেলিভারি ঘণ্টা", ["Failed %"] = "ব্যর্থ %", ["Returned %"] = "ফেরত %", ["Cost per parcel"] = "প্রতি পার্সেল খরচ",
        ["Cash collected"] = "সংগৃহীত নগদ", ["Cash settled"] = "নিষ্পত্তি নগদ", ["Cash pending"] = "বকেয়া নগদ", ["Customers"] = "গ্রাহক", ["New customers"] = "নতুন গ্রাহক",
        ["Returning customers"] = "পুরনো গ্রাহক", ["Revenue"] = "আয়", ["Top spenders"] = "সেরা ক্রেতা", ["Customer"] = "গ্রাহক", ["Mobile"] = "মোবাইল", ["Spent"] = "খরচ করেছেন",
        ["Conversion"] = "রূপান্তর", ["Guests created"] = "নতুন গেস্ট", ["Converted to profile"] = "প্রোফাইলে রূপান্তর", ["Conversion %"] = "রূপান্তর %",
        ["Placed"] = "অর্ডার হয়েছে", ["Confirmed"] = "নিশ্চিত", ["Packed"] = "প্যাক", ["Shipped"] = "পাঠানো", ["Cancelled"] = "বাতিল", ["Returned"] = "ফেরত এসেছে",
        ["Shop"] = "দোকান", ["Notes"] = "নোট", ["Date"] = "তারিখ", ["Kind"] = "ধরন", ["Description"] = "বিবরণ", ["Customer receipts"] = "গ্রাহকের পেমেন্ট",
    };

    public static string T(string english, string lang) => lang == "bn" && Bn.TryGetValue(english, out var bn) ? bn : english;

    public static bool IsLang(string? lang) => lang is null or "en" or "bn";
}
