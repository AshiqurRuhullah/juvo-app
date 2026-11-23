using System.Text.RegularExpressions;

namespace Juvo.Application.Monitoring;

/// <summary>
/// Makes an error text safe to show to staff (FR-AF02, NFR-012): keeps the first line only (never a stack trace), hides keys, tokens, passwords, bearer and signature values,
/// e-mail addresses and phone numbers, and cuts the text to a fixed length. Ids (GUIDs) stay readable so a row can be traced.
/// </summary>
public static partial class ErrorScrubber
{
    public const int DefaultMaxLength = 300;
    public const string Redacted = "[hidden]";

    [GeneratedRegex(@"(?i)\b(bearer|basic)\s+[A-Za-z0-9._~+/=\-]+")]
    private static partial Regex AuthScheme();

    [GeneratedRegex(@"(?i)\b(api[_\-]?key|apikey|secret|token|password|passwd|pwd|authorization|signature|sig|credential|client[_\-]?secret|access[_\-]?key|store[_\-]?passwd)\b(\s*[:=]\s*)(""[^""]*""|'[^']*'|[^\s,;&]+)")]
    private static partial Regex KeyValue();

    [GeneratedRegex(@"[A-Za-z0-9._%+\-]+@[A-Za-z0-9.\-]+\.[A-Za-z]{2,}")]
    private static partial Regex Email();

    [GeneratedRegex(@"\+?\d[\d\s\-]{7,}\d")]
    private static partial Regex Phone();

    [GeneratedRegex(@"[A-Za-z0-9_\-+/=]{32,}")]
    private static partial Regex LongToken();

    [GeneratedRegex(@"([0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12})")]
    private static partial Regex Guid();

    [GeneratedRegex(@"[\r\n]+")]
    private static partial Regex Lines();

    /// <summary>Null or blank input gives null.</summary>
    public static string? Scrub(string? text, int maxLength = DefaultMaxLength)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        // First non-empty line that is not a stack frame. Everything after it (inner exceptions, frames) is dropped.
        var first = Lines().Split(text.Trim()).Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0 && !l.StartsWith("at ", StringComparison.Ordinal)) ?? string.Empty;
        var cut = first.IndexOf(" at Juvo.", StringComparison.Ordinal);
        if (cut > 0)
        {
            first = first[..cut];
        }

        first = AuthScheme().Replace(first, m => $"{m.Groups[1].Value} {Redacted}");
        first = KeyValue().Replace(first, m => $"{m.Groups[1].Value}{m.Groups[2].Value}{Redacted}");
        first = Email().Replace(first, Redacted);

        // Ids stay readable: only the text between GUIDs is checked for long tokens and phone numbers.
        var parts = Guid().Split(first);
        for (var i = 0; i < parts.Length; i += 2)
        {
            parts[i] = LongToken().Replace(parts[i], Redacted);
            parts[i] = Phone().Replace(parts[i], m => m.Value.Count(char.IsDigit) >= 8 ? Redacted : m.Value);
        }

        first = string.Concat(parts);
        if (first.Length > maxLength)
        {
            first = first[..(maxLength - 1)] + "…";
        }

        return first.Length == 0 ? null : first;
    }
}
