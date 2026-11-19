using Juvo.Domain.Common;

namespace Juvo.Domain.Content;

/// <summary>The fixed set of editable pages (FR-Q01). The key is also the public address of the page.</summary>
public static class ContentPageKeys
{
    public const string About = "about";
    public const string Faq = "faq";
    public const string DeliveryPolicy = "delivery-policy";
    public const string ReturnPolicy = "return-policy";
    public const string PrivacyPolicy = "privacy-policy";
    public const string Terms = "terms";

    public static readonly IReadOnlyList<string> All = [About, Faq, DeliveryPolicy, ReturnPolicy, PrivacyPolicy, Terms];

    public static string? Normalize(string? key)
    {
        var value = key?.Trim().ToLowerInvariant();
        return value is not null && All.Contains(value) ? value : null;
    }
}

/// <summary>The fixed set of trust badges (FR-Q07).</summary>
public static class TrustBadgeKeys
{
    public const string SecurePayment = "secure-payment";
    public const string DeliveryCoverage = "delivery-coverage";
    public const string ReturnPolicy = "return-policy";
    public const string SupportHours = "support-hours";

    public static readonly IReadOnlyList<string> All = [SecurePayment, DeliveryCoverage, ReturnPolicy, SupportHours];

    public static string? Normalize(string? key)
    {
        var value = key?.Trim().ToLowerInvariant();
        return value is not null && All.Contains(value) ? value : null;
    }
}

internal static class ContentText
{
    public static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

/// <summary>A static page of the shop in English and Bengali (FR-Q01). The body is sanitised markdown. A page is shown to customers only while published.</summary>
public sealed class ContentPage : Entity
{
    private ContentPage()
    {
    }

    public ContentPage(string key)
    {
        Key = key;
    }

    public string Key { get; private set; } = string.Empty;

    public string? TitleEn { get; private set; }

    public string? TitleBn { get; private set; }

    public string? BodyEn { get; private set; }

    public string? BodyBn { get; private set; }

    public string? MetaTitleEn { get; private set; }

    public string? MetaTitleBn { get; private set; }

    public string? MetaDescriptionEn { get; private set; }

    public string? MetaDescriptionBn { get; private set; }

    public bool IsPublished { get; private set; }

    public DateTimeOffset? PublishedAt { get; private set; }

    /// <summary>A page can go live only with an English title and an English body (Bengali is optional and falls back to English in the shop).</summary>
    public bool CanPublish => !string.IsNullOrWhiteSpace(TitleEn) && !string.IsNullOrWhiteSpace(BodyEn);

    public void Update(string? titleEn, string? titleBn, string? bodyEn, string? bodyBn, string? metaTitleEn, string? metaTitleBn, string? metaDescriptionEn, string? metaDescriptionBn)
    {
        TitleEn = ContentText.Clean(titleEn);
        TitleBn = ContentText.Clean(titleBn);
        BodyEn = ContentText.Clean(bodyEn);
        BodyBn = ContentText.Clean(bodyBn);
        MetaTitleEn = ContentText.Clean(metaTitleEn);
        MetaTitleBn = ContentText.Clean(metaTitleBn);
        MetaDescriptionEn = ContentText.Clean(metaDescriptionEn);
        MetaDescriptionBn = ContentText.Clean(metaDescriptionBn);
        if (IsPublished && !CanPublish)
        {
            IsPublished = false; // emptying the content takes the page down rather than showing a blank page
        }
    }

    /// <summary>Returns false (and changes nothing) when the page is not complete enough to publish.</summary>
    public bool Publish(DateTimeOffset now)
    {
        if (!CanPublish)
        {
            return false;
        }

        if (!IsPublished)
        {
            PublishedAt = now;
        }

        IsPublished = true;
        return true;
    }

    public void Unpublish() => IsPublished = false;
}

/// <summary>The one row of brand and contact details (FR-Q02). Everything may be empty until the owner fills it in.</summary>
public sealed class BrandProfile : Entity
{
    private BrandProfile()
    {
    }

    public BrandProfile(string businessName, string? email)
    {
        BusinessName = businessName;
        Email = ContentText.Clean(email);
    }

    public string BusinessName { get; private set; } = string.Empty;

    /// <summary>A file reference as returned by the upload service (relative, served under /files).</summary>
    public string? LogoFileRef { get; private set; }

    public string? AddressEn { get; private set; }

    public string? AddressBn { get; private set; }

    public string? Phone { get; private set; }

    /// <summary>Normalised to +8801XXXXXXXXX (or international) form.</summary>
    public string? WhatsApp { get; private set; }

    public string? Email { get; private set; }

    public string? WorkingHoursEn { get; private set; }

    public string? WorkingHoursBn { get; private set; }

    public decimal? Latitude { get; private set; }

    public decimal? Longitude { get; private set; }

    public void Update(string businessName, string? logoFileRef, string? addressEn, string? addressBn, string? phone, string? whatsApp, string? email, string? workingHoursEn, string? workingHoursBn, decimal? latitude, decimal? longitude)
    {
        BusinessName = businessName.Trim();
        LogoFileRef = ContentText.Clean(logoFileRef);
        AddressEn = ContentText.Clean(addressEn);
        AddressBn = ContentText.Clean(addressBn);
        Phone = ContentText.Clean(phone);
        WhatsApp = ContentText.Clean(whatsApp);
        Email = ContentText.Clean(email);
        WorkingHoursEn = ContentText.Clean(workingHoursEn);
        WorkingHoursBn = ContentText.Clean(workingHoursBn);
        Latitude = latitude;
        Longitude = longitude;
    }
}

public enum SocialPlatform
{
    Facebook = 1,
    Instagram = 2,
    TikTok = 3,
    YouTube = 4,
    WhatsApp = 5,
}

/// <summary>A link to one social account, shown in the footer and on the contact page (FR-Q03). One row per platform.</summary>
public sealed class SocialLink : Entity
{
    private SocialLink()
    {
    }

    public SocialLink(SocialPlatform platform, string url, bool isEnabled, int sortOrder)
    {
        Platform = platform;
        Url = url;
        IsEnabled = isEnabled;
        SortOrder = sortOrder;
    }

    public SocialPlatform Platform { get; private set; }

    public string Url { get; private set; } = string.Empty;

    public bool IsEnabled { get; private set; }

    public int SortOrder { get; private set; }

    public void Update(string url, bool isEnabled, int sortOrder)
    {
        Url = url;
        IsEnabled = isEnabled;
        SortOrder = sortOrder;
    }
}

/// <summary>A short reassurance line near checkout and in the footer (FR-Q07). The four keys exist from the start; a badge is shown only while enabled and worded.</summary>
public sealed class TrustBadge : Entity
{
    private TrustBadge()
    {
    }

    public TrustBadge(string key, int sortOrder)
    {
        Key = key;
        SortOrder = sortOrder;
    }

    public string Key { get; private set; } = string.Empty;

    public string? TextEn { get; private set; }

    public string? TextBn { get; private set; }

    public bool IsEnabled { get; private set; }

    public int SortOrder { get; private set; }

    /// <summary>A badge without English text cannot be enabled.</summary>
    public void Update(string? textEn, string? textBn, bool isEnabled, int sortOrder)
    {
        TextEn = ContentText.Clean(textEn);
        TextBn = ContentText.Clean(textBn);
        IsEnabled = isEnabled && TextEn is not null;
        SortOrder = sortOrder;
    }
}
