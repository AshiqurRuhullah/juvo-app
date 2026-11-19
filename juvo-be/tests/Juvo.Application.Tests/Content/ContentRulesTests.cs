using Juvo.Application.Content;
using Juvo.Domain.Content;

namespace Juvo.Application.Tests.Content;

public class ContentRulesTests
{
    [Theory]
    [InlineData("<script>alert(1)</script>")]
    [InlineData("Hello <img src=x onerror=alert(1)>")]
    [InlineData("<SCRIPT src=//evil></SCRIPT>")]
    [InlineData("text < script>x")]
    [InlineData("<!-- hidden -->")]
    [InlineData("<iframe src=\"https://evil\"></iframe>")]
    [InlineData("[click](javascript:alert(1))")]
    [InlineData("[click]( JavaScript:alert(1))")]
    [InlineData("[x]: data:text/html;base64,AAAA")]
    [InlineData("![x](vbscript:run)")]
    [InlineData("<javascript:alert(1)>")]
    public void Raw_html_script_and_dangerous_links_are_rejected(string body) => Assert.NotNull(ContentRules.CheckBody(body));

    [Theory]
    [InlineData("")]
    [InlineData("# About us\n\nWe sell **fashion**.\n\n- one\n- two\n\n[Facebook](https://facebook.com/juvobd)")]
    [InlineData("Price < 500 and > 100 are fine, 2 < 3.")]
    [InlineData("আমাদের সম্পর্কে\n\nআমরা পোশাক বিক্রি করি।")]
    public void Plain_markdown_is_accepted(string body) => Assert.Null(ContentRules.CheckBody(body));

    [Fact]
    public void A_body_over_the_limit_is_rejected() => Assert.NotNull(ContentRules.CheckBody(new string('a', ContentRules.BodyMax + 1)));

    [Fact]
    public void Line_endings_are_normalised_and_the_body_is_trimmed() =>
        Assert.Equal("a\nb\nc", ContentRules.NormalizeBody("  a\r\nb\rc \n"));

    [Theory]
    [InlineData("https://facebook.com/juvobd", true)]
    [InlineData("https://www.instagram.com/juvobd/", true)]
    [InlineData("http://facebook.com/juvobd", false)]
    [InlineData("javascript:alert(1)", false)]
    [InlineData("https://user:pass@facebook.com/x", false)]
    [InlineData("//facebook.com/x", false)]
    [InlineData("https://localhost/x", false)]
    [InlineData("https://face book.com/x", false)]
    [InlineData("", false)]
    public void Social_addresses_must_be_https(string url, bool valid) => Assert.Equal(valid, ContentRules.IsHttpsUrl(url, out _));

    [Fact]
    public void A_whatsapp_number_becomes_a_wa_me_address()
    {
        Assert.Null(ContentRules.CheckSocialUrl(SocialPlatform.WhatsApp, "01712345678", out var url));
        Assert.Equal("https://wa.me/8801712345678", url);

        Assert.Null(ContentRules.CheckSocialUrl(SocialPlatform.WhatsApp, "https://wa.me/8801712345678", out var kept));
        Assert.Equal("https://wa.me/8801712345678", kept);

        Assert.NotNull(ContentRules.CheckSocialUrl(SocialPlatform.WhatsApp, "12345", out _));
    }

    [Fact]
    public void A_number_is_not_accepted_for_the_other_platforms() =>
        Assert.NotNull(ContentRules.CheckSocialUrl(SocialPlatform.Facebook, "01712345678", out _));

    [Theory]
    [InlineData("juvobd@gmail.com", true)]
    [InlineData("a@b", false)]
    [InlineData("not an email", false)]
    [InlineData("Name <a@b.com>", false)]
    [InlineData("", false)]
    public void Email_is_checked(string email, bool valid) => Assert.Equal(valid, ContentRules.IsEmail(email));

    [Theory]
    [InlineData("+880 1712-345678", true)]
    [InlineData("(02) 9876543", true)]
    [InlineData("call me", false)]
    [InlineData("123", false)]
    public void A_shop_phone_line_is_checked_loosely(string phone, bool valid) => Assert.Equal(valid, ContentRules.IsPhoneText(phone));

    [Theory]
    [InlineData("content/logo.webp", true)]
    [InlineData("../secret.txt", false)]
    [InlineData("https://evil/x.png", false)]
    [InlineData("/etc/passwd", false)]
    [InlineData("", false)]
    public void A_logo_is_a_relative_file_reference(string value, bool valid) => Assert.Equal(valid, ContentRules.IsFileRef(value));
}
