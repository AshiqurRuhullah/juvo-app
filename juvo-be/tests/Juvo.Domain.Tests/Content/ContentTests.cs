using Juvo.Domain.Content;

namespace Juvo.Domain.Tests.Content;

public class ContentTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 19, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public void The_fixed_page_and_badge_keys_are_the_ones_of_the_specification()
    {
        Assert.Equal(["about", "faq", "delivery-policy", "return-policy", "privacy-policy", "terms"], ContentPageKeys.All);
        Assert.Equal(["secure-payment", "delivery-coverage", "return-policy", "support-hours"], TrustBadgeKeys.All);
        Assert.Equal("delivery-policy", ContentPageKeys.Normalize("  Delivery-Policy "));
        Assert.Null(ContentPageKeys.Normalize("pricing"));
        Assert.Null(TrustBadgeKeys.Normalize(null));
    }

    [Fact]
    public void A_new_page_is_an_empty_draft_that_cannot_be_published()
    {
        var page = new ContentPage(ContentPageKeys.About);

        Assert.False(page.IsPublished);
        Assert.False(page.CanPublish);
        Assert.False(page.Publish(Now));
        Assert.False(page.IsPublished);
        Assert.Null(page.PublishedAt);
    }

    [Fact]
    public void A_page_with_an_english_title_and_body_publishes_and_remembers_when_first()
    {
        var page = new ContentPage(ContentPageKeys.Faq);
        page.Update("Questions", null, "Body", null, null, null, null, null);

        Assert.True(page.Publish(Now));
        Assert.True(page.IsPublished);
        Assert.True(page.Publish(Now.AddDays(1)));
        Assert.Equal(Now, page.PublishedAt);
    }

    [Fact]
    public void Bengali_text_alone_is_not_enough_to_publish()
    {
        var page = new ContentPage(ContentPageKeys.Terms);
        page.Update(null, "শর্তাবলী", null, "লেখা", null, null, null, null);

        Assert.False(page.CanPublish);
    }

    [Fact]
    public void Emptying_a_published_page_takes_it_down_and_blanks_become_null()
    {
        var page = new ContentPage(ContentPageKeys.About);
        page.Update("About", "  ", "Story", "", null, null, null, null);
        page.Publish(Now);

        page.Update("About", null, "   ", null, null, null, null, null);

        Assert.False(page.IsPublished);
        Assert.Null(page.BodyEn);
        Assert.Null(page.TitleBn);
    }

    [Fact]
    public void Unpublish_returns_a_page_to_draft()
    {
        var page = new ContentPage(ContentPageKeys.About);
        page.Update("About", null, "Story", null, null, null, null, null);
        page.Publish(Now);

        page.Unpublish();

        Assert.False(page.IsPublished);
    }

    [Fact]
    public void A_badge_without_english_text_cannot_be_enabled()
    {
        var badge = new TrustBadge(TrustBadgeKeys.SecurePayment, 1);

        badge.Update(null, "বাংলা", true, 1);
        Assert.False(badge.IsEnabled);

        badge.Update(" Secure payment ", null, true, 2);
        Assert.True(badge.IsEnabled);
        Assert.Equal("Secure payment", badge.TextEn);
        Assert.Equal(2, badge.SortOrder);
    }

    [Fact]
    public void The_brand_profile_trims_empty_values_to_null()
    {
        var brand = new BrandProfile("Juvo BD", " juvobd@gmail.com ");
        Assert.Equal("juvobd@gmail.com", brand.Email);

        brand.Update(" Juvo BD ", " ", "Dhaka", null, "", null, null, "  ", null, 23.8m, 90.4m);

        Assert.Equal("Juvo BD", brand.BusinessName);
        Assert.Null(brand.LogoFileRef);
        Assert.Null(brand.Phone);
        Assert.Null(brand.Email);
        Assert.Equal("Dhaka", brand.AddressEn);
        Assert.Equal(23.8m, brand.Latitude);
    }
}
