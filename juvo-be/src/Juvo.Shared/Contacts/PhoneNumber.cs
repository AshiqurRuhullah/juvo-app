using System.Text.RegularExpressions;

namespace Juvo.Shared.Contacts;

/// <summary>
/// Mobile numbers are the customer identity (BR-006). They are stored in international format: <c>+8801XXXXXXXXX</c> for
/// Bangladesh, or <c>+&lt;country&gt;&lt;number&gt;</c> for other countries (SRS FR-D03).
/// </summary>
public static partial class PhoneNumber
{
    [GeneratedRegex(@"^\+8801[3-9]\d{8}$")]
    private static partial Regex BangladeshMobile();

    [GeneratedRegex(@"^\+[1-9]\d{7,14}$")]
    private static partial Regex InternationalE164();

    /// <summary>Returns the normalized number, or null when the input is not a valid mobile number.</summary>
    public static string? Normalize(string? input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return null;
        }

        var cleaned = new string(input.Where(c => char.IsDigit(c) || c == '+').ToArray());
        if (cleaned.Length == 0)
        {
            return null;
        }

        string candidate;
        if (cleaned.StartsWith("+880", StringComparison.Ordinal))
        {
            candidate = cleaned;
        }
        else if (cleaned.StartsWith("00880", StringComparison.Ordinal))
        {
            candidate = "+" + cleaned[2..];
        }
        else if (cleaned.StartsWith("880", StringComparison.Ordinal) && cleaned.Length == 13)
        {
            candidate = "+" + cleaned;
        }
        else if (cleaned.StartsWith("01", StringComparison.Ordinal) && cleaned.Length == 11)
        {
            candidate = "+88" + cleaned;
        }
        else if (cleaned.StartsWith('+'))
        {
            candidate = cleaned;
        }
        else if (cleaned.StartsWith("00", StringComparison.Ordinal))
        {
            candidate = "+" + cleaned[2..];
        }
        else
        {
            return null;
        }

        if (candidate.StartsWith("+880", StringComparison.Ordinal))
        {
            return BangladeshMobile().IsMatch(candidate) ? candidate : null;
        }

        return InternationalE164().IsMatch(candidate) ? candidate : null;
    }

    public static bool IsBangladesh(string normalized) => normalized.StartsWith("+880", StringComparison.Ordinal);
}
