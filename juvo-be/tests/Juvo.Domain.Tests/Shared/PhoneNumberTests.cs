using Juvo.Shared.Contacts;

namespace Juvo.Domain.Tests.Shared;

public class PhoneNumberTests
{
    [Theory]
    [InlineData("01712345678", "+8801712345678")]
    [InlineData("017 1234 5678", "+8801712345678")]
    [InlineData("017-1234-5678", "+8801712345678")]
    [InlineData("8801712345678", "+8801712345678")]
    [InlineData("+8801712345678", "+8801712345678")]
    [InlineData("008801712345678", "+8801712345678")]
    [InlineData("+880 1712-345678", "+8801712345678")]
    [InlineData("+14155552671", "+14155552671")]
    [InlineData("0014155552671", "+14155552671")]
    [InlineData("+919876543210", "+919876543210")]
    public void Valid_numbers_are_normalized_to_international_format(string input, string expected) =>
        Assert.Equal(expected, PhoneNumber.Normalize(input));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("abc")]
    [InlineData("0171234567")]       // too short
    [InlineData("017123456789")]     // too long
    [InlineData("01212345678")]      // 012 is not a Bangladesh mobile prefix
    [InlineData("+8801212345678")]
    [InlineData("12345")]
    [InlineData("+0123456789")]
    [InlineData("+1")]
    public void Invalid_numbers_return_null(string? input) => Assert.Null(PhoneNumber.Normalize(input));

    [Fact]
    public void Bangladesh_numbers_are_detected()
    {
        Assert.True(PhoneNumber.IsBangladesh("+8801712345678"));
        Assert.False(PhoneNumber.IsBangladesh("+14155552671"));
    }
}
