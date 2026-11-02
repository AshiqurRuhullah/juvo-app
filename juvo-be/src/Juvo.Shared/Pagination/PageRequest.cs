namespace Juvo.Shared.Pagination;

/// <summary>
/// Consistent list contract for every list endpoint:
/// <c>?page=1&amp;pageSize=20&amp;sort=name,-createdAt&amp;search=text&amp;filter=status:eq:active</c>.
/// </summary>
public sealed record PageRequest
{
    public const int DefaultPageSize = 20;
    public const int MaxPageSize = 100;

    public int Page { get; init; } = 1;

    public int PageSize { get; init; } = DefaultPageSize;

    /// <summary>Comma separated field names; prefix with <c>-</c> for descending.</summary>
    public string? Sort { get; init; }

    /// <summary>Free text search across the fields the endpoint supports.</summary>
    public string? Search { get; init; }

    /// <summary>Comma separated <c>field:operator:value</c> items (operators: eq, ne, gt, gte, lt, lte, contains).</summary>
    public string? Filter { get; init; }

    /// <summary>The same request with page &gt;= 1 and pageSize between 1 and <see cref="MaxPageSize"/>.</summary>
    public PageRequest Normalized => this with
    {
        Page = Page < 1 ? 1 : Page,
        PageSize = PageSize < 1 ? DefaultPageSize : Math.Min(PageSize, MaxPageSize),
    };

    public int Skip
    {
        get
        {
            var n = Normalized;
            return (n.Page - 1) * n.PageSize;
        }
    }

    /// <summary>Parses <see cref="Sort"/>; fields not in <paramref name="allowedFields"/> are ignored.</summary>
    public IReadOnlyList<SortField> ParseSort(IReadOnlySet<string>? allowedFields = null)
    {
        if (string.IsNullOrWhiteSpace(Sort))
        {
            return [];
        }

        var fields = new List<SortField>();
        foreach (var raw in Sort.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var descending = raw.StartsWith('-');
            var name = raw.TrimStart('-', '+');
            if (name.Length == 0 || !IsAllowed(allowedFields, name))
            {
                continue;
            }

            fields.Add(new SortField(name, descending));
        }

        return fields;
    }

    /// <summary>Parses <see cref="Filter"/>; malformed items and fields not in <paramref name="allowedFields"/> are ignored.</summary>
    public IReadOnlyList<FilterItem> ParseFilter(IReadOnlySet<string>? allowedFields = null)
    {
        if (string.IsNullOrWhiteSpace(Filter))
        {
            return [];
        }

        var items = new List<FilterItem>();
        foreach (var raw in Filter.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = raw.Split(':', 3);
            if (parts.Length != 3 || !FilterOperators.All.Contains(parts[1], StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!IsAllowed(allowedFields, parts[0]))
            {
                continue;
            }

            items.Add(new FilterItem(parts[0], parts[1].ToLowerInvariant(), parts[2]));
        }

        return items;
    }

    private static bool IsAllowed(IReadOnlySet<string>? allowed, string name) =>
        allowed is null || allowed.Contains(name, StringComparer.OrdinalIgnoreCase);
}

public sealed record SortField(string Name, bool Descending);

public sealed record FilterItem(string Field, string Operator, string Value);

public static class FilterOperators
{
    public static readonly IReadOnlyList<string> All = ["eq", "ne", "gt", "gte", "lt", "lte", "contains"];
}
