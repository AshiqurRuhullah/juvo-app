using Juvo.Domain.Customers;

namespace Juvo.Domain.Tests.Privacy;

public class AnonymiseCustomerTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 23, 9, 0, 0, TimeSpan.Zero);

    private static Customer Person()
    {
        var customer = new Customer("+8801712345678", ProfileLevel.Full);
        customer.UpdateProfile("Nadia Rahman", "nadia@example.com");
        customer.VerifyMobile(Now.AddDays(-3));
        customer.SetPreferredLanguage("bn");
        return customer;
    }

    [Fact]
    public void Anonymising_replaces_every_personal_field_and_deactivates()
    {
        var customer = Person();

        customer.Anonymise(Now);

        Assert.True(customer.IsAnonymised);
        Assert.Equal(Now, customer.AnonymisedAt);
        Assert.False(customer.IsActive);
        Assert.False(customer.MobileVerified);
        Assert.Equal(ProfileLevel.Guest, customer.ProfileLevel);
        Assert.Equal(PrivacyPlaceholders.Name, customer.FullName);
        Assert.Null(customer.Email);
        Assert.Null(customer.LastLoginAt);
        Assert.Null(customer.PreferredLanguage);
        Assert.DoesNotContain("8801712345678", customer.Mobile, StringComparison.Ordinal);
        Assert.True(PrivacyPlaceholders.IsPlaceholderMobile(customer.Mobile));
    }

    [Fact]
    public void Anonymising_twice_changes_nothing()
    {
        var customer = Person();
        customer.Anonymise(Now);
        var mobile = customer.Mobile;

        customer.Anonymise(Now.AddDays(1));

        Assert.Equal(Now, customer.AnonymisedAt);
        Assert.Equal(mobile, customer.Mobile);
    }

    [Fact]
    public void The_placeholder_mobile_fits_the_column_is_stable_unique_and_never_a_real_number()
    {
        var a = Guid.CreateVersion7();
        var b = Guid.CreateVersion7();

        Assert.Equal(20, PrivacyPlaceholders.MobileFor(a).Length);
        Assert.Equal(PrivacyPlaceholders.MobileFor(a), PrivacyPlaceholders.MobileFor(a));
        Assert.NotEqual(PrivacyPlaceholders.MobileFor(a), PrivacyPlaceholders.MobileFor(b));
        Assert.False(PrivacyPlaceholders.MobileFor(a).StartsWith('+'));
        Assert.False(PrivacyPlaceholders.IsPlaceholderMobile("+8801712345678"));
        Assert.False(PrivacyPlaceholders.IsPlaceholderMobile(null));
    }

    [Fact]
    public void An_anonymised_customer_is_not_reported_for_a_normal_one()
    {
        Assert.False(Person().IsAnonymised);
        Assert.Null(Person().AnonymisedAt);
    }
}
