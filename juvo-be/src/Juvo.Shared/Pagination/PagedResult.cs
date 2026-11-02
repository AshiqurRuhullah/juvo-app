namespace Juvo.Shared.Pagination;

/// <summary>Response shape of every list endpoint: <c>items, total, page, pageSize</c>.</summary>
public sealed record PagedResult<T>(IReadOnlyList<T> Items, int Total, int Page, int PageSize)
{
    public int TotalPages => PageSize <= 0 ? 0 : (int)Math.Ceiling(Total / (double)PageSize);

    public static PagedResult<T> Create(IReadOnlyList<T> items, int total, PageRequest request)
    {
        var normalized = request.Normalized;
        return new PagedResult<T>(items, total, normalized.Page, normalized.PageSize);
    }

    public PagedResult<TOut> Map<TOut>(Func<T, TOut> selector) =>
        new([.. Items.Select(selector)], Total, Page, PageSize);
}
