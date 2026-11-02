using Juvo.Shared.Pagination;

namespace Juvo.Domain.Tests.Shared;

public class PageRequestTests
{
    [Theory]
    [InlineData(0, 0, 1, 20)]
    [InlineData(-3, 500, 1, 100)]
    [InlineData(3, 10, 3, 10)]
    public void Normalized_clamps_page_and_page_size(int page, int pageSize, int expectedPage, int expectedSize)
    {
        var n = new PageRequest { Page = page, PageSize = pageSize }.Normalized;

        Assert.Equal(expectedPage, n.Page);
        Assert.Equal(expectedSize, n.PageSize);
    }

    [Fact]
    public void Skip_uses_normalized_values()
    {
        Assert.Equal(40, new PageRequest { Page = 3, PageSize = 20 }.Skip);
        Assert.Equal(0, new PageRequest { Page = -1 }.Skip);
    }

    [Fact]
    public void ParseSort_reads_direction_and_ignores_unknown_fields()
    {
        var request = new PageRequest { Sort = "name,-createdAt,secret" };
        var allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "name", "createdAt" };

        var sort = request.ParseSort(allowed);

        Assert.Equal(2, sort.Count);
        Assert.Equal(new SortField("name", false), sort[0]);
        Assert.Equal(new SortField("createdAt", true), sort[1]);
    }

    [Fact]
    public void ParseFilter_reads_field_operator_value_and_drops_bad_items()
    {
        var request = new PageRequest { Filter = "status:eq:active,price:gte:100,oops,x:bad:1" };

        var filter = request.ParseFilter();

        Assert.Equal(2, filter.Count);
        Assert.Equal(new FilterItem("status", "eq", "active"), filter[0]);
        Assert.Equal(new FilterItem("price", "gte", "100"), filter[1]);
    }

    [Fact]
    public void PagedResult_reports_total_pages_and_maps_items()
    {
        var page = PagedResult<int>.Create([1, 2, 3], 45, new PageRequest { Page = 2, PageSize = 20 });

        Assert.Equal(3, page.TotalPages);
        Assert.Equal(2, page.Page);
        Assert.Equal(["1", "2", "3"], page.Map(i => i.ToString(System.Globalization.CultureInfo.InvariantCulture)).Items);
    }
}
