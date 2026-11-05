using Juvo.Application.Catalog;

namespace Juvo.Application.Tests.Catalog;

public class SlugRulesTests
{
    [Theory]
    [InlineData("Ladies' Dresses", "ladies-dresses")]
    [InlineData("  T-Shirt   (Cotton) ", "t-shirt-cotton")]
    [InlineData("পোশাক Party Wear", "party-wear")]
    [InlineData("পোশাক", "")]
    public void Slugify_keeps_ascii_words_joined_by_single_hyphens(string input, string expected) =>
        Assert.Equal(expected, SlugRules.Slugify(input));

    [Theory]
    [InlineData("tee", true)]
    [InlineData("tee-2", true)]
    [InlineData("-tee", false)]
    [InlineData("tee-", false)]
    [InlineData("a--b", false)]
    [InlineData("Tee", false)]
    [InlineData("", false)]
    public void IsValid_accepts_only_clean_slugs(string slug, bool expected) => Assert.Equal(expected, SlugRules.IsValid(slug));

    [Fact]
    public async Task Resolve_numbers_a_generated_slug_until_it_is_free()
    {
        var taken = new HashSet<string> { "red-tee", "red-tee-2" };

        var result = await SlugRules.ResolveAsync(null, "Red Tee", null, (s, _, _) => Task.FromResult(taken.Contains(s)), TestContext.Current.CancellationToken);

        Assert.Equal("red-tee-3", result.Value);
    }

    [Fact]
    public async Task Resolve_refuses_a_requested_slug_that_is_taken_or_invalid()
    {
        var ct = TestContext.Current.CancellationToken;

        var taken = await SlugRules.ResolveAsync("red-tee", "x", null, (_, _, _) => Task.FromResult(true), ct);
        var invalid = await SlugRules.ResolveAsync("Red Tee", "x", null, (_, _, _) => Task.FromResult(false), ct);
        var fallback = await SlugRules.ResolveAsync(null, "পোশাক", null, (_, _, _) => Task.FromResult(false), ct);

        Assert.Equal("CATALOG.SLUG_TAKEN", taken.Error!.Code);
        Assert.Equal("GENERAL.VALIDATION", invalid.Error!.Code);
        Assert.Equal("item", fallback.Value);
    }
}
