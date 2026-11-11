using Juvo.Application.Storefront;

namespace Juvo.Application.Tests.Storefront;

public class TextMatchTests
{
    [Fact]
    public void Tokens_are_lower_case_words_split_on_punctuation_and_keep_bengali()
    {
        Assert.Equal(["cotton", "t", "shirt", "ফ্লোরাল"], TextMatch.Tokens("Cotton T-Shirt, ফ্লোরাল"));
        Assert.Empty(TextMatch.Tokens("   "));
        Assert.Empty(TextMatch.Tokens(null));
    }

    [Theory]
    [InlineData("shirt", "shirt", 0)]
    [InlineData("shrit", "shirt", 1)]   // two neighbouring letters swapped count as one mistake
    [InlineData("sandle", "sandal", 2)]
    [InlineData("dress", "dresses", 2)]
    public void Distance_counts_insertions_deletions_and_replacements(string a, string b, int expected)
    {
        Assert.Equal(expected, TextMatch.Distance(a, b, 5));
    }

    [Fact]
    public void Distance_stops_early_when_the_words_are_too_different()
    {
        Assert.True(TextMatch.Distance("wallet", "jacket", 1) > 1);
        Assert.True(TextMatch.Distance("a", "abcdefgh", 2) > 2);
    }

    [Theory]
    [InlineData("tee", 0)]
    [InlineData("shirt", 1)]
    [InlineData("jacket", 2)]
    [InlineData("jackets", 2)]
    public void Short_words_get_no_mistakes_and_long_words_more(string word, int expected)
    {
        Assert.Equal(expected, TextMatch.Tolerance(word));
    }

    [Theory]
    [InlineData("shrit", "classic cotton shirts", true)]   // typo of the start of "shirts"
    [InlineData("jaket", "denim jacket", true)]
    [InlineData("sneekers", "everyday canvas sneakers", true)]
    [InlineData("wallet", "denim jacket", false)]
    [InlineData("tee", "classic cotton t shirt", false)]    // three letters must match exactly
    [InlineData("deni", "denim jacket", true)]              // a start of a word
    public void Matches_allows_small_spelling_mistakes(string query, string words, bool expected)
    {
        Assert.Equal(expected, TextMatch.Matches(query, TextMatch.Tokens(words)));
    }
}
