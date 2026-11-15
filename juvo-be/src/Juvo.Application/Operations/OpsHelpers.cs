using Juvo.Application.Orders;
using Juvo.Domain.Branches;
using Juvo.Shared.Results;

namespace Juvo.Application.Operations;

/// <summary>Small helpers shared by the purchasing, accounting, support and report services.</summary>
internal static class Ops
{
    /// <summary>Bangladesh Standard Time (UTC+6), the clock of every report and of every date-only field that has no branch of its own.</summary>
    public static readonly TimeSpan Bst = TimeSpan.FromHours(6);

    public static Error Invalid(string field, string message) => Error.Validation(new Dictionary<string, string[]> { [field] = [message] });

    public static DateOnly LocalDate(Branch branch, DateTimeOffset now) => DateOnly.FromDateTime(OrderPlacement.LocalTime(branch, now).DateTime);

    public static DateOnly BstDate(DateTimeOffset at) => DateOnly.FromDateTime(at.ToOffset(Bst).DateTime);

    /// <summary>The first instant of the day in Bangladesh time.</summary>
    public static DateTimeOffset StartOfDay(DateOnly day) => new(day.Year, day.Month, day.Day, 0, 0, 0, Bst);

    public static string? Clean(string? text, int max) => string.IsNullOrWhiteSpace(text) ? null : text.Trim().Length > max ? text.Trim()[..max] : text.Trim();

    public static bool TooLong(string? text, int max) => text is not null && text.Trim().Length > max;
}
