using Juvo.Application.Abstractions;
using Juvo.Domain.Catalog;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Juvo.Application.Storefront;

/// <summary>Settings of the customer-facing shop. Section: <c>Storefront</c>.</summary>
public sealed class StorefrontOptions
{
    public const string SectionName = "Storefront";

    /// <summary>
    /// Branch codes whose stock the shop sells online. Empty means every active branch (assumptions K1). Customers only ever see the
    /// combined number, never which branch holds it.
    /// </summary>
    public List<string> FulfilmentBranchCodes { get; set; } = [];

    /// <summary>At or below this many units (and above zero) a variant is shown as low stock.</summary>
    public int LowStockThreshold { get; set; } = 3;

    /// <summary>Largest availability number shown to a customer ("10" means ten or more).</summary>
    public int MaxQuantityShown { get; set; } = 10;

    public int MaxQuantityPerLine { get; set; } = 10;

    public int MaxCartLines { get; set; } = 50;

    /// <summary>A cart expires this many days after its last change.</summary>
    public int CartDays { get; set; } = 30;

    public int MaxWishlistItems { get; set; } = 200;
}

/// <summary>Combined availability of variants across the branches that sell online (available = on hand minus reserved).</summary>
public sealed class StockAvailability(IApplicationDbContext db, IOptions<StorefrontOptions> options)
{
    public StorefrontOptions Options => options.Value;

    public async Task<List<Guid>> BranchIdsAsync(CancellationToken cancellationToken)
    {
        var codes = options.Value.FulfilmentBranchCodes.Select(c => c.Trim().ToUpperInvariant()).Where(c => c.Length > 0).ToList();
        return await db.Branches.AsNoTracking().Where(b => b.IsActive && (codes.Count == 0 || codes.Contains(b.Code))).Select(b => b.Id).ToListAsync(cancellationToken);
    }

    /// <summary>Available units per variant (zero when never stocked). Variants that do not track stock are not in the result.</summary>
    public async Task<Dictionary<Guid, int>> AvailableAsync(IReadOnlyCollection<Guid> variantIds, CancellationToken cancellationToken)
    {
        if (variantIds.Count == 0)
        {
            return [];
        }

        var branches = await BranchIdsAsync(cancellationToken);
        var rows = await db.StockLevels.AsNoTracking()
            .Where(l => variantIds.Contains(l.VariantId) && branches.Contains(l.BranchId))
            .GroupBy(l => l.VariantId)
            .Select(g => new { VariantId = g.Key, Available = g.Sum(l => l.OnHand - l.Reserved) })
            .ToListAsync(cancellationToken);
        var result = variantIds.ToDictionary(id => id, _ => 0);
        foreach (var row in rows)
        {
            result[row.VariantId] = Math.Max(0, row.Available);
        }

        return result;
    }

    /// <summary>The customer-facing number: capped, and a variant that does not track stock counts as plenty.</summary>
    public int Shown(ProductVariant variant, IReadOnlyDictionary<Guid, int> available) =>
        variant.TracksStock ? Math.Min(available.GetValueOrDefault(variant.Id), options.Value.MaxQuantityShown) : options.Value.MaxQuantityShown;

    /// <summary>The real quantity a customer may order (not capped for display, but never above the per-line limit).</summary>
    public int Orderable(ProductVariant variant, IReadOnlyDictionary<Guid, int> available) =>
        Math.Min(variant.TracksStock ? available.GetValueOrDefault(variant.Id) : options.Value.MaxQuantityPerLine, options.Value.MaxQuantityPerLine);

    public bool IsLow(ProductVariant variant, IReadOnlyDictionary<Guid, int> available)
    {
        if (!variant.TracksStock)
        {
            return false;
        }

        var units = available.GetValueOrDefault(variant.Id);
        return units > 0 && units <= options.Value.LowStockThreshold;
    }
}

/// <summary>Small helpers for spelling-tolerant search (FR-A05).</summary>
public static class TextMatch
{
    /// <summary>Lower-case words of letters and digits (Bengali letters included).</summary>
    public static List<string> Tokens(string? text) =>
        string.IsNullOrWhiteSpace(text)
            ? []
            : [.. text.ToLowerInvariant().Split([' ', ',', '-', '_', '/', '.', '(', ')', '\t', '\n'], StringSplitOptions.RemoveEmptyEntries).Where(t => t.Length > 0)];

    /// <summary>Edit distance between two words (insert, delete, replace), stopping early once it is above <paramref name="max"/>.</summary>
    public static int Distance(string a, string b, int max)
    {
        if (Math.Abs(a.Length - b.Length) > max)
        {
            return max + 1;
        }

        var previous = new int[b.Length + 1];
        var current = new int[b.Length + 1];
        for (var j = 0; j <= b.Length; j++)
        {
            previous[j] = j;
        }

        for (var i = 1; i <= a.Length; i++)
        {
            current[0] = i;
            var rowMin = current[0];
            for (var j = 1; j <= b.Length; j++)
            {
                var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                current[j] = Math.Min(Math.Min(current[j - 1] + 1, previous[j] + 1), previous[j - 1] + cost);
                rowMin = Math.Min(rowMin, current[j]);
            }

            if (rowMin > max)
            {
                return max + 1;
            }

            (previous, current) = (current, previous);
        }

        return previous[b.Length];
    }

    /// <summary>One mistake is allowed in words of 4 to 6 letters, two in longer words, none in shorter ones.</summary>
    public static int Tolerance(string word) => word.Length switch { <= 3 => 0, <= 6 => 1, _ => 2 };

    /// <summary>True when <paramref name="query"/> is close to a word, or a start of a word, in <paramref name="words"/>.</summary>
    public static bool Matches(string query, IEnumerable<string> words)
    {
        var tolerance = Tolerance(query);
        foreach (var word in words)
        {
            if (word.StartsWith(query, StringComparison.Ordinal) || Distance(query, word, tolerance) <= tolerance)
            {
                return true;
            }

            // A short typed word may be a typo of the start of a longer word ("shrit" for "shirts").
            if (word.Length > query.Length && tolerance > 0 && Distance(query, word[..Math.Min(word.Length, query.Length)], tolerance) <= tolerance)
            {
                return true;
            }
        }

        return false;
    }
}
