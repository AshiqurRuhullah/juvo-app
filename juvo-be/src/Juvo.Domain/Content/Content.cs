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

/// <summary>Where a contact-form message stands in the admin inbox (FR-Q06).</summary>
public enum ContactMessageStatus
{
    New = 1,
    Read = 2,
    Replied = 3,
    Archived = 4,
}

/// <summary>
/// A message sent from the public contact page (FR-Q06). The sender gives a mobile number or an email (at least one). Personal data is kept out of the audit trail
/// and the client address is stored only as a hash. Messages are never erased: staff archive them.
/// </summary>
public sealed class ContactMessage : Entity
{
    public const int InternalNoteMax = 4000;

    private ContactMessage()
    {
    }

    public ContactMessage(string name, string? mobile, string? email, string? subject, string message, string? sourcePage, string? ipHash, DateTimeOffset now)
    {
        if (ContentText.Clean(mobile) is null && ContentText.Clean(email) is null)
        {
            throw new ArgumentException("A mobile number or an email address is required.", nameof(mobile));
        }

        Name = name.Trim();
        Mobile = ContentText.Clean(mobile);
        Email = ContentText.Clean(email);
        Subject = ContentText.Clean(subject);
        Message = message.Trim();
        SourcePage = ContentText.Clean(sourcePage);
        IpHash = ipHash;
        ReceivedAt = now;
        Status = ContactMessageStatus.New;
    }

    [AuditIgnore]
    public string Name { get; private set; } = string.Empty;

    /// <summary>Normalised (+880...) mobile number, or null when the sender left an email only.</summary>
    [AuditIgnore]
    public string? Mobile { get; private set; }

    [AuditIgnore]
    public string? Email { get; private set; }

    public string? Subject { get; private set; }

    [AuditIgnore]
    public string Message { get; private set; } = string.Empty;

    public ContactMessageStatus Status { get; private set; }

    /// <summary>The shop page the form was sent from (a path such as <c>/contact</c>).</summary>
    public string? SourcePage { get; private set; }

    public DateTimeOffset ReceivedAt { get; private set; }

    /// <summary>SHA-256 of the client address (never the address itself), used only to limit abuse.</summary>
    [AuditIgnore]
    public string? IpHash { get; private set; }

    public Guid? HandledBy { get; private set; }

    public DateTimeOffset? HandledAt { get; private set; }

    [AuditIgnore]
    public string? InternalNote { get; private set; }

    /// <summary>The short reference given to the sender (last eight hex digits of the id).</summary>
    public string Reference => "CM-" + Id.ToString("N")[^8..].ToUpperInvariant();

    /// <summary>Moves to a status (any move is allowed, so a mistaken archive can be undone). Returns false when nothing changed.</summary>
    public bool SetStatus(ContactMessageStatus status, Guid? by, DateTimeOffset now)
    {
        if (Status == status)
        {
            return false;
        }

        Status = status;
        if (status == ContactMessageStatus.New)
        {
            HandledBy = null;
            HandledAt = null;
        }
        else
        {
            HandledBy = by;
            HandledAt = now;
        }

        return true;
    }

    /// <summary>Appends a note (with who and when) to the internal note. Returns false when the result would be longer than <see cref="InternalNoteMax"/>.</summary>
    public bool AddNote(string text, string? authorName, DateTimeOffset now)
    {
        var line = $"[{now:yyyy-MM-dd HH:mm} UTC, {authorName ?? "staff"}] {text.Trim()}";
        var next = InternalNote is null ? line : InternalNote + "\n" + line;
        if (next.Length > InternalNoteMax)
        {
            return false;
        }

        InternalNote = next;
        return true;
    }
}
