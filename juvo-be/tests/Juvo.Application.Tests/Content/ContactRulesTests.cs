using Juvo.Application.Content;

namespace Juvo.Application.Tests.Content;

public class ContactRulesTests
{
    private static ContactFormRequest Form(string? name = "Nadia", string? mobile = "01712345678", string? email = null, string? subject = null, string? message = "Where is my parcel?", string? page = "/contact") =>
        new(name, mobile, email, subject, message, page, null);

    [Fact]
    public void A_valid_form_with_a_mobile_passes_and_the_mobile_is_normalised()
    {
        var (errors, mobile, email) = ContactRules.Validate(Form());

        Assert.Empty(errors);
        Assert.Equal("+8801712345678", mobile);
        Assert.Null(email);
    }

    [Fact]
    public void An_email_alone_is_enough_and_is_lower_cased()
    {
        var (errors, mobile, email) = ContactRules.Validate(Form(mobile: null, email: "Nadia@Example.COM"));

        Assert.Empty(errors);
        Assert.Null(mobile);
        Assert.Equal("nadia@example.com", email);
    }

    [Fact]
    public void Neither_mobile_nor_email_is_refused()
    {
        var (errors, _, _) = ContactRules.Validate(Form(mobile: null, email: " "));

        Assert.True(errors.ContainsKey("contact"));
    }

    [Theory]
    [InlineData("name", "<b>x</b>")]
    [InlineData("subject", "hi <script>alert(1)</script>")]
    [InlineData("message", "look <img src=x onerror=alert(1)>")]
    public void Markup_is_rejected_in_every_text_field(string field, string value)
    {
        var request = field switch
        {
            "name" => Form(name: value),
            "subject" => Form(subject: value),
            _ => Form(message: value),
        };

        var (errors, _, _) = ContactRules.Validate(request);

        Assert.True(errors.ContainsKey(field));
    }

    [Fact]
    public void Lengths_and_formats_are_checked()
    {
        Assert.True(ContactRules.Validate(Form(name: new string('a', ContactRules.NameMax + 1))).Errors.ContainsKey("name"));
        Assert.True(ContactRules.Validate(Form(subject: new string('a', ContactRules.SubjectMax + 1))).Errors.ContainsKey("subject"));
        Assert.True(ContactRules.Validate(Form(message: "hi")).Errors.ContainsKey("message"));
        Assert.True(ContactRules.Validate(Form(message: new string('a', ContactRules.MessageMax + 1))).Errors.ContainsKey("message"));
        Assert.True(ContactRules.Validate(Form(mobile: "123")).Errors.ContainsKey("mobile"));
        Assert.True(ContactRules.Validate(Form(mobile: null, email: "nope")).Errors.ContainsKey("email"));
        Assert.True(ContactRules.Validate(Form(page: "https://evil.example")).Errors.ContainsKey("sourcePage"));
        Assert.True(ContactRules.Validate(Form(page: "//evil.example")).Errors.ContainsKey("sourcePage"));
    }

    [Fact]
    public void The_client_address_is_stored_only_as_a_hash()
    {
        var hash = ContactRules.HashClient("203.0.113.9");

        Assert.NotNull(hash);
        Assert.Equal(64, hash!.Length);
        Assert.DoesNotContain("203", hash, StringComparison.Ordinal);
        Assert.Equal(hash, ContactRules.HashClient("203.0.113.9"));
        Assert.NotEqual(hash, ContactRules.HashClient("203.0.113.10"));
        Assert.Null(ContactRules.HashClient(null));
    }
}
