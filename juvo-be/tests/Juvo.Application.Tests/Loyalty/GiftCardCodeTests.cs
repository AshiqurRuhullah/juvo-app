using Juvo.Application.Loyalty;

namespace Juvo.Application.Tests.Loyalty;

public class GiftCardCodeTests
{
    [Fact]
    public void New_codes_use_the_safe_alphabet_have_sixteen_characters_in_four_groups_and_do_not_repeat()
    {
        var codes = Enumerable.Range(0, 500).Select(_ => GiftCardCodes.NewCode()).ToList();
        Assert.All(codes, c => Assert.Matches("^[A-HJ-NP-Z2-9]{4}(-[A-HJ-NP-Z2-9]{4}){3}$", c)); // no 0, 1, I or O
        Assert.Equal(codes.Count, codes.Distinct().Count());
    }

    [Theory]
    [InlineData("abcd-efgh-jklm-npqr", "ABCDEFGHJKLMNPQR")]
    [InlineData("  ABCD EFGH\tJKLM-NPQR ", "ABCDEFGHJKLMNPQR")]
    [InlineData(null, "")]
    [InlineData("", "")]
    [InlineData("--//", "")]
    public void A_typed_code_is_normalised_whatever_the_case_spaces_or_dashes(string? typed, string expected) => Assert.Equal(expected, GiftCardCodes.Normalize(typed));

    [Fact]
    public void The_hash_is_stable_hex_and_differs_for_a_different_code_and_the_hint_is_the_last_four()
    {
        var a = GiftCardCodes.Hash("ABCDEFGHJKLMNPQR");
        Assert.Equal(a, GiftCardCodes.Hash(GiftCardCodes.Normalize("abcd-efgh-jklm-npqr")));
        Assert.Matches("^[0-9a-f]{64}$", a);
        Assert.NotEqual(a, GiftCardCodes.Hash("ABCDEFGHJKLMNPQS"));
        Assert.Equal("NPQR", GiftCardCodes.Hint("ABCDEFGHJKLMNPQR"));
        Assert.Equal("AB", GiftCardCodes.Hint("AB"));
    }
}
