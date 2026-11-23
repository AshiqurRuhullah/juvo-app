using System.Globalization;
using System.Net.Mail;
using System.Text.Json;
using Juvo.Shared.Results;

namespace Juvo.Application.Settings;

public enum SettingType
{
    Bool = 1,
    Whole = 2,
    Number = 3,
    Text = 4,
    Choice = 5,
}

/// <summary>Extra shape rules for a string setting.</summary>
public enum SettingFormat
{
    None = 0,

    /// <summary>A clock time written <c>HH:mm</c>.</summary>
    TimeOfDay = 1,

    /// <summary>An email address, or empty.</summary>
    Email = 2,

    /// <summary>An absolute http or https address, or empty.</summary>
    Url = 3,
}

/// <summary>
/// One runtime setting known to the settings centre. The value itself lives in <c>PlatformSettings</c> (the same row the owning module reads), so the centre and the module
/// can never disagree. A definition says how a value is checked and who may change it; <see cref="Default"/> must equal what the reading module uses when no row exists.
/// </summary>
public sealed record SettingDefinition(
    string Key, string Group, SettingType Type, string Default, string DescriptionEn, string DescriptionBn, string EditPermission,
    decimal? Min = null, decimal? Max = null, IReadOnlyList<string>? AllowedValues = null, int MaxLength = 500, SettingFormat Format = SettingFormat.None,
    bool RequiresRestart = false, bool Sensitive = false)
{
    /// <summary>The value as the owning module would store it, or the reason it is not acceptable.</summary>
    public Result<string> Normalize(string? raw)
    {
        static Result<string> Bad(string message) =>
            new Error(ErrorCodes.Settings.InvalidValue, message, ErrorType.Validation, new Dictionary<string, string[]> { ["value"] = [message] });

        var text = raw?.Trim() ?? string.Empty;
        switch (Type)
        {
            case SettingType.Bool:
                if (string.Equals(text, "true", StringComparison.OrdinalIgnoreCase))
                {
                    return "true";
                }

                return string.Equals(text, "false", StringComparison.OrdinalIgnoreCase) ? "false" : Bad("Use true or false.");

            case SettingType.Whole:
                if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var whole))
                {
                    return Bad("Use a whole number.");
                }

                return InRange(whole, out var intProblem) ? whole.ToString(CultureInfo.InvariantCulture) : Bad(intProblem);

            case SettingType.Number:
                if (!decimal.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out var number))
                {
                    return Bad("Use a number.");
                }

                if (decimal.Round(number, 4) != number)
                {
                    return Bad("Use at most 4 decimals.");
                }

                return InRange(number, out var decimalProblem) ? number.ToString("0.####", CultureInfo.InvariantCulture) : Bad(decimalProblem);

            case SettingType.Choice:
                var match = AllowedValues?.FirstOrDefault(v => string.Equals(v, text, StringComparison.OrdinalIgnoreCase));
                return match ?? Bad("Use one of: " + string.Join(", ", AllowedValues ?? []) + ".");

            default:
                if (text.Length > MaxLength)
                {
                    return Bad($"Use at most {MaxLength} characters.");
                }

                return FormatProblem(text) is { } problem ? Bad(problem) : text;
        }
    }

    /// <summary>The value as a typed JSON value (bool, number or text) for the API.</summary>
    public object Typed(string raw) => Type switch
    {
        SettingType.Bool => bool.TryParse(raw, out var b) && b,
        SettingType.Whole => int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i) ? i : 0,
        SettingType.Number => decimal.TryParse(raw, NumberStyles.Number, CultureInfo.InvariantCulture, out var d) ? d : 0m,
        _ => raw,
    };

    public string TypeName => Type.ToString().ToLowerInvariant();

    private bool InRange(decimal value, out string problem)
    {
        problem = string.Empty;
        if (Min is { } min && value < min)
        {
            problem = $"Use {min.ToString("0.####", CultureInfo.InvariantCulture)} or more.";
            return false;
        }

        if (Max is { } max && value > max)
        {
            problem = $"Use {max.ToString("0.####", CultureInfo.InvariantCulture)} or less.";
            return false;
        }

        return true;
    }

    private string? FormatProblem(string text)
    {
        if (text.Length == 0)
        {
            return Format == SettingFormat.TimeOfDay ? "Use a time written HH:mm." : null;
        }

        switch (Format)
        {
            case SettingFormat.TimeOfDay:
                return TimeOnly.TryParseExact(text, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out _) ? null : "Use a time written HH:mm.";
            case SettingFormat.Email:
                return MailAddress.TryCreate(text, out var address) && address.Address == text && text.Contains('@') ? null : "Use a valid email address.";
            case SettingFormat.Url:
                return Uri.TryCreate(text, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" ? null : "Use a full address starting with http:// or https://.";
            default:
                return null;
        }
    }

    /// <summary>Turns a JSON value (true, 12, 3.5, "text") into the text the setting stores. Null when the JSON kind is not a plain value.</summary>
    public static string? FromJson(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        JsonValueKind.Number => value.GetRawText(),
        JsonValueKind.String => value.GetString(),
        _ => null,
    };
}
