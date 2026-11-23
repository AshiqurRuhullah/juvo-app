namespace Juvo.Domain.Customers;

/// <summary>
/// The neutral values written over personal data when a customer is anonymised (FR-E08, FR-AF06). One place, so every table uses the same words
/// and a reader can tell "removed on request" from "never given".
/// </summary>
public static class PrivacyPlaceholders
{
    public const string Name = "Anonymised";

    /// <summary>Replaces free text (notes, messages, subjects, reasons).</summary>
    public const string Text = "[removed on request]";

    /// <summary>Replaces an address line.</summary>
    public const string Address = "[removed]";

    /// <summary>Replaces a notification body or other long text kept for delivery logs.</summary>
    public const string Body = "[anonymised]";

    /// <summary>The prefix of a placeholder mobile number. Real numbers are stored as +880..., so a placeholder can never equal one.</summary>
    public const string MobilePrefix = "ANON";

    /// <summary>Valid JSON written over the old and new values of audit rows that held personal data.</summary>
    public const string AuditJson = "{\"anonymised\":true}";

    /// <summary>
    /// A unique 20-character placeholder for one customer (the mobile column is 20 characters wide): <c>ANON</c> and the last 16 hex digits of the
    /// customer id, which are random in a version 7 id. The same id always gives the same value.
    /// </summary>
    public static string MobileFor(Guid customerId) => MobilePrefix + customerId.ToString("N")[^16..].ToUpperInvariant();

    public static bool IsPlaceholderMobile(string? mobile) => mobile is { Length: 20 } && mobile.StartsWith(MobilePrefix, StringComparison.Ordinal);
}
