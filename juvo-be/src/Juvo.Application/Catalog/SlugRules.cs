using System.Text;
using Juvo.Shared.Results;

namespace Juvo.Application.Catalog;

/// <summary>Builds URL slugs (lower case ASCII, hyphens) and makes them unique.</summary>
public static class SlugRules
{
    public const int MaxLength = 150;

    /// <summary>Lower-case letters and digits joined by single hyphens. Characters outside a-z and 0-9 (including Bengali) separate words.</summary>
    public static string Slugify(string? text)
    {
        var builder = new StringBuilder();
        var pendingHyphen = false;
        foreach (var ch in (text ?? string.Empty).Trim().ToLowerInvariant())
        {
            if (ch is >= 'a' and <= 'z' or >= '0' and <= '9')
            {
                if (pendingHyphen && builder.Length > 0)
                {
                    builder.Append('-');
                }

                pendingHyphen = false;
                builder.Append(ch);
            }
            else
            {
                pendingHyphen = true;
            }

            if (builder.Length >= MaxLength)
            {
                break;
            }
        }

        return builder.ToString();
    }

    public static bool IsValid(string slug) =>
        slug.Length is > 0 and <= MaxLength && !slug.StartsWith('-') && !slug.EndsWith('-') && !slug.Contains("--", StringComparison.Ordinal) &&
        slug.All(ch => ch is >= 'a' and <= 'z' or >= '0' and <= '9' or '-');

    /// <summary>
    /// A given slug must be valid and free (otherwise a conflict). Without one, a slug is made from the English name and a number
    /// is appended until it is free.
    /// </summary>
    public static async Task<Result<string>> ResolveAsync(
        string? requested, string nameEn, Guid? exceptId, Func<string, Guid, CancellationToken, Task<bool>> isTaken, CancellationToken cancellationToken)
    {
        var except = exceptId ?? Guid.Empty;
        if (!string.IsNullOrWhiteSpace(requested))
        {
            var slug = requested.Trim().ToLowerInvariant();
            if (!IsValid(slug))
            {
                return Error.Validation(new Dictionary<string, string[]> { ["slug"] = ["Use lower-case letters, digits and single hyphens."] });
            }

            return await isTaken(slug, except, cancellationToken)
                ? Error.Conflict(ErrorCodes.Catalog.SlugTaken, "This slug is already used.")
                : slug;
        }

        var baseSlug = Slugify(nameEn);
        if (baseSlug.Length == 0)
        {
            baseSlug = "item";
        }

        var candidate = baseSlug;
        for (var n = 2; await isTaken(candidate, except, cancellationToken); n++)
        {
            var suffix = "-" + n;
            candidate = (baseSlug.Length + suffix.Length > MaxLength ? baseSlug[..(MaxLength - suffix.Length)] : baseSlug) + suffix;
        }

        return candidate;
    }
}
