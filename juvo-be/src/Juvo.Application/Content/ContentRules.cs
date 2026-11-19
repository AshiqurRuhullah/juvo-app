using System.Net.Mail;
using System.Text.RegularExpressions;
using Juvo.Domain.Content;
using Juvo.Shared.Contacts;
using Juvo.Shared.Results;

namespace Juvo.Application.Content;

/// <summary>
/// Validation and sanitising for site content (module Q). Page bodies are stored as markdown. Decision: raw HTML is not allowed at all, and a body that contains any is
/// rejected with <c>CONTENT.BODY_UNSAFE</c> (not silently stripped), so staff see what is wrong and the stored text is exactly what they typed. Links with a
/// <c>javascript:</c>, <c>data:</c> or <c>vbscript:</c> address are rejected too. The shop renders markdown without raw HTML.
/// </summary>
public static partial class ContentRules
{
    public const int TitleMax = 200;
    public const int BodyMax = 30_000;
    public const int MetaTitleMax = 200;
    public const int MetaDescriptionMax = 400;
    public const int BusinessNameMax = 120;
    public const int AddressMax = 500;
    public const int HoursMax = 300;
    public const int UrlMax = 500;
    public const int BadgeTextMax = 160;
    public const int FileRefMax = 300;

    // A "<" followed by a letter, "/", "!" or "?" starts an HTML tag, comment or processing instruction.
    [GeneratedRegex(@"<\s*[a-zA-Z/!?]")]
    private static partial Regex HtmlTag();

    // Inside markdown links and images: [text](javascript:...) and also reference style "[x]: javascript:...".
    [GeneratedRegex(@"(?i)(\]\(\s*|\]:\s*|<\s*)(javascript|data|vbscript)\s*:")]
    private static partial Regex DangerousLink();

    [GeneratedRegex(@"^[0-9+\-\s()]{6,20}$")]
    private static partial Regex LoosePhone();

    public static Error Invalid(string field, string message) =>
        new(ErrorCodes.Content.Invalid, "The content is not valid.", ErrorType.Validation, new Dictionary<string, string[]> { [field] = [message] });

    public static Error Invalid(Dictionary<string, string[]> errors) =>
        new(ErrorCodes.Content.Invalid, "The content is not valid.", ErrorType.Validation, errors);

    /// <summary>Checks a markdown body. Returns an error message, or null when the body is acceptable. Line endings are normalised by <see cref="NormalizeBody"/>.</summary>
    public static string? CheckBody(string? body)
    {
        if (string.IsNullOrEmpty(body))
        {
            return null;
        }

        if (body.Length > BodyMax)
        {
            return $"The text can have at most {BodyMax} characters.";
        }

        if (HtmlTag().IsMatch(body) || DangerousLink().IsMatch(body))
        {
            return "HTML and script are not allowed. Use plain markdown (headings, lists, bold, links to https addresses).";
        }

        return null;
    }

    /// <summary>True when a short plain-text field carries an HTML tag.</summary>
    public static bool LooksLikeMarkup(string? text) => !string.IsNullOrEmpty(text) && HtmlTag().IsMatch(text);

    public static string? NormalizeBody(string? body) => body?.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Trim();

    public static void CheckPlain(Dictionary<string, string[]> errors, string field, string? value, int max)
    {
        if (value is null)
        {
            return;
        }

        if (value.Length > max)
        {
            errors[field] = [$"At most {max} characters."];
        }
        else if (LooksLikeMarkup(value))
        {
            errors[field] = ["HTML is not allowed in this field."];
        }
    }

    /// <summary>A web address for a social link: absolute, https only, no user name or password, no line breaks.</summary>
    public static bool IsHttpsUrl(string? value, out string normalized)
    {
        normalized = string.Empty;
        if (string.IsNullOrWhiteSpace(value) || value.Length > UrlMax || value.Any(char.IsWhiteSpace))
        {
            return false;
        }

        if (!Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps || !string.IsNullOrEmpty(uri.UserInfo) || string.IsNullOrEmpty(uri.Host) || !uri.Host.Contains('.', StringComparison.Ordinal))
        {
            return false;
        }

        normalized = uri.AbsoluteUri;
        return true;
    }

    /// <summary>The cleaned social address for a platform, or an error message. WhatsApp also accepts a plain mobile number, which becomes <c>https://wa.me/&lt;digits&gt;</c>.</summary>
    public static string? CheckSocialUrl(SocialPlatform platform, string? input, out string normalized)
    {
        normalized = string.Empty;
        if (platform == SocialPlatform.WhatsApp)
        {
            if (PhoneNumber.Normalize(input) is { } phone)
            {
                normalized = "https://wa.me/" + phone.TrimStart('+');
                return null;
            }
        }

        return IsHttpsUrl(input, out normalized) ? null : platform == SocialPlatform.WhatsApp
            ? "Enter a mobile number or an https address (for example https://wa.me/8801XXXXXXXXX)."
            : "Enter a full https address (for example https://facebook.com/juvobd).";
    }

    public static bool IsEmail(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 254)
        {
            return false;
        }

        try
        {
            var address = new MailAddress(value.Trim());
            return address.Address == value.Trim() && address.Host.Contains('.', StringComparison.Ordinal);
        }
        catch (FormatException)
        {
            return false;
        }
    }

    /// <summary>A shop phone line may be a landline, so it is checked loosely (digits, spaces, +, -, brackets).</summary>
    public static bool IsPhoneText(string? value) => value is not null && LoosePhone().IsMatch(value.Trim());

    /// <summary>A file reference from the upload service: relative, no traversal, no scheme.</summary>
    public static bool IsFileRef(string? value) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= FileRefMax && !value.Contains("..", StringComparison.Ordinal) && !value.Contains(':', StringComparison.Ordinal)
        && !value.StartsWith('/') && !value.StartsWith('\\') && !value.Any(char.IsControl);
}
