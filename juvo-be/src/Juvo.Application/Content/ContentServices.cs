using Juvo.Application.Abstractions;
using Juvo.Domain.Content;
using Juvo.Shared.Contacts;
using Juvo.Shared.Results;
using Microsoft.EntityFrameworkCore;

namespace Juvo.Application.Content;

/// <summary>Staff side of the site content (module Q): pages, brand profile, social links and trust badges. Edits are audited by the persistence layer like every entity.</summary>
public sealed class ContentAdminService(IApplicationDbContext db, TimeProvider clock)
{
    private static Error PageNotFound() => Error.NotFound(ErrorCodes.Content.NotFound, "Page not found.");

    private static PageDto ToDto(ContentPage p) => new(
        p.Key, p.TitleEn, p.TitleBn, p.BodyEn, p.BodyBn, p.MetaTitleEn, p.MetaTitleBn, p.MetaDescriptionEn, p.MetaDescriptionBn, p.IsPublished, p.CanPublish, p.PublishedAt, p.UpdatedAt ?? p.CreatedAt);

    private static BrandDto ToDto(BrandProfile b) => new(
        b.BusinessName, b.LogoFileRef, b.AddressEn, b.AddressBn, b.Phone, b.WhatsApp, b.Email, b.WorkingHoursEn, b.WorkingHoursBn, b.Latitude, b.Longitude, b.UpdatedAt ?? b.CreatedAt);

    private static SocialLinkDto ToDto(SocialLink s) => new(s.Platform, s.Url, s.IsEnabled, s.SortOrder, s.UpdatedAt ?? s.CreatedAt);

    private static TrustBadgeDto ToDto(TrustBadge b) => new(b.Key, b.TextEn, b.TextBn, b.IsEnabled, b.SortOrder, b.UpdatedAt ?? b.CreatedAt);

    // ---- pages (FR-Q01) ----

    public async Task<Result<IReadOnlyList<PageDto>>> ListPagesAsync(CancellationToken cancellationToken)
    {
        var rows = await db.ContentPages.AsNoTracking().ToListAsync(cancellationToken);
        return rows.OrderBy(p => ContentPageKeys.All.ToList().IndexOf(p.Key)).Select(ToDto).ToList();
    }

    public async Task<Result<PageDto>> GetPageAsync(string key, CancellationToken cancellationToken)
    {
        var page = await FindPageAsync(key, false, cancellationToken);
        return page is null ? PageNotFound() : ToDto(page);
    }

    public async Task<Result<PageDto>> UpdatePageAsync(string key, UpdatePageRequest r, CancellationToken cancellationToken)
    {
        var page = await FindPageAsync(key, true, cancellationToken);
        if (page is null)
        {
            return PageNotFound();
        }

        var errors = new Dictionary<string, string[]>();
        ContentRules.CheckPlain(errors, "titleEn", r.TitleEn, ContentRules.TitleMax);
        ContentRules.CheckPlain(errors, "titleBn", r.TitleBn, ContentRules.TitleMax);
        ContentRules.CheckPlain(errors, "metaTitleEn", r.MetaTitleEn, ContentRules.MetaTitleMax);
        ContentRules.CheckPlain(errors, "metaTitleBn", r.MetaTitleBn, ContentRules.MetaTitleMax);
        ContentRules.CheckPlain(errors, "metaDescriptionEn", r.MetaDescriptionEn, ContentRules.MetaDescriptionMax);
        ContentRules.CheckPlain(errors, "metaDescriptionBn", r.MetaDescriptionBn, ContentRules.MetaDescriptionMax);
        if (ContentRules.CheckBody(r.BodyEn) is { } bodyEn)
        {
            errors["bodyEn"] = [bodyEn];
        }

        if (ContentRules.CheckBody(r.BodyBn) is { } bodyBn)
        {
            errors["bodyBn"] = [bodyBn];
        }

        if (errors.Count > 0)
        {
            var unsafeBody = errors.ContainsKey("bodyEn") || errors.ContainsKey("bodyBn");
            return new Error(unsafeBody ? ErrorCodes.Content.BodyUnsafe : ErrorCodes.Content.Invalid, "The content is not valid.", ErrorType.Validation, errors);
        }

        page.Update(r.TitleEn, r.TitleBn, ContentRules.NormalizeBody(r.BodyEn), ContentRules.NormalizeBody(r.BodyBn), r.MetaTitleEn, r.MetaTitleBn, r.MetaDescriptionEn, r.MetaDescriptionBn);
        await db.SaveChangesAsync(cancellationToken);
        return ToDto(page);
    }

    public async Task<Result<PageDto>> PublishPageAsync(string key, bool publish, CancellationToken cancellationToken)
    {
        var page = await FindPageAsync(key, true, cancellationToken);
        if (page is null)
        {
            return PageNotFound();
        }

        if (publish)
        {
            if (!page.Publish(clock.GetUtcNow()))
            {
                return new Error(ErrorCodes.Content.PageIncomplete, "A page needs an English title and an English text before it can be published.", ErrorType.Validation,
                    new Dictionary<string, string[]> { ["titleEn"] = ["Required to publish."], ["bodyEn"] = ["Required to publish."] });
            }
        }
        else
        {
            page.Unpublish();
        }

        await db.SaveChangesAsync(cancellationToken);
        return ToDto(page);
    }

    private async Task<ContentPage?> FindPageAsync(string key, bool track, CancellationToken cancellationToken)
    {
        var normalized = ContentPageKeys.Normalize(key);
        if (normalized is null)
        {
            return null;
        }

        var query = track ? db.ContentPages : db.ContentPages.AsNoTracking();
        return await query.FirstOrDefaultAsync(p => p.Key == normalized, cancellationToken);
    }

    // ---- brand profile (FR-Q02) ----

    public async Task<Result<BrandDto>> GetBrandAsync(CancellationToken cancellationToken)
    {
        var brand = await db.BrandProfiles.AsNoTracking().OrderBy(x => x.CreatedAt).FirstOrDefaultAsync(cancellationToken);
        return brand is null ? Error.NotFound(ErrorCodes.Content.NotFound, "The brand profile has not been created yet.") : ToDto(brand);
    }

    public async Task<Result<BrandDto>> UpdateBrandAsync(UpdateBrandRequest r, CancellationToken cancellationToken)
    {
        var brand = await db.BrandProfiles.OrderBy(x => x.CreatedAt).FirstOrDefaultAsync(cancellationToken);
        if (brand is null)
        {
            return Error.NotFound(ErrorCodes.Content.NotFound, "The brand profile has not been created yet.");
        }

        var errors = new Dictionary<string, string[]>();
        var name = r.BusinessName?.Trim();
        if (string.IsNullOrEmpty(name))
        {
            errors["businessName"] = ["Required."];
        }

        ContentRules.CheckPlain(errors, "businessName", name, ContentRules.BusinessNameMax);
        ContentRules.CheckPlain(errors, "addressEn", r.AddressEn, ContentRules.AddressMax);
        ContentRules.CheckPlain(errors, "addressBn", r.AddressBn, ContentRules.AddressMax);
        ContentRules.CheckPlain(errors, "workingHoursEn", r.WorkingHoursEn, ContentRules.HoursMax);
        ContentRules.CheckPlain(errors, "workingHoursBn", r.WorkingHoursBn, ContentRules.HoursMax);

        if (!string.IsNullOrWhiteSpace(r.LogoFileRef) && !ContentRules.IsFileRef(r.LogoFileRef.Trim()))
        {
            errors["logoFileRef"] = ["Use the file reference returned by the upload, not an address."];
        }

        if (!string.IsNullOrWhiteSpace(r.Phone) && !ContentRules.IsPhoneText(r.Phone))
        {
            errors["phone"] = ["Use digits, spaces, + and - only (6 to 20 characters)."];
        }

        string? whatsApp = null;
        if (!string.IsNullOrWhiteSpace(r.WhatsApp))
        {
            whatsApp = PhoneNumber.Normalize(r.WhatsApp);
            if (whatsApp is null)
            {
                errors["whatsApp"] = ["Enter a valid mobile number, for example 01712345678."];
            }
        }

        if (!string.IsNullOrWhiteSpace(r.Email) && !ContentRules.IsEmail(r.Email))
        {
            errors["email"] = ["Enter a valid email address."];
        }

        if (r.Latitude is null != r.Longitude is null)
        {
            errors["latitude"] = ["Give both latitude and longitude, or neither."];
        }
        else if (r.Latitude is { } lat && r.Longitude is { } lng)
        {
            if (lat is < -90m or > 90m)
            {
                errors["latitude"] = ["Between -90 and 90."];
            }

            if (lng is < -180m or > 180m)
            {
                errors["longitude"] = ["Between -180 and 180."];
            }
        }

        if (errors.Count > 0)
        {
            return ContentRules.Invalid(errors);
        }

        brand.Update(name!, r.LogoFileRef?.Trim(), r.AddressEn, r.AddressBn, r.Phone?.Trim(), whatsApp, r.Email?.Trim(), r.WorkingHoursEn, r.WorkingHoursBn,
            r.Latitude is { } la ? Math.Round(la, 6) : null, r.Longitude is { } lo ? Math.Round(lo, 6) : null);
        await db.SaveChangesAsync(cancellationToken);
        return ToDto(brand);
    }

    // ---- social links (FR-Q03) ----

    public async Task<Result<IReadOnlyList<SocialLinkDto>>> ListSocialAsync(CancellationToken cancellationToken)
    {
        var rows = await db.SocialLinks.AsNoTracking().OrderBy(s => s.SortOrder).ThenBy(s => s.Platform).ToListAsync(cancellationToken);
        return rows.Select(ToDto).ToList();
    }

    /// <summary>Creates or changes the link of one platform.</summary>
    public async Task<Result<SocialLinkDto>> SaveSocialAsync(string platformName, SaveSocialLinkRequest r, CancellationToken cancellationToken)
    {
        if (!Enum.TryParse<SocialPlatform>(platformName, true, out var platform) || !Enum.IsDefined(platform))
        {
            return Error.NotFound(ErrorCodes.Content.NotFound, "Unknown social platform.");
        }

        if (ContentRules.CheckSocialUrl(platform, r.Url, out var url) is { } message)
        {
            return new Error(ErrorCodes.Content.UrlInvalid, "The address is not valid.", ErrorType.Validation, new Dictionary<string, string[]> { ["url"] = [message] });
        }

        if (r.SortOrder is < 0 or > 1000)
        {
            return ContentRules.Invalid("sortOrder", "Between 0 and 1000.");
        }

        var link = await db.SocialLinks.FirstOrDefaultAsync(s => s.Platform == platform, cancellationToken);
        if (link is null)
        {
            link = new SocialLink(platform, url, r.IsEnabled, r.SortOrder);
            db.SocialLinks.Add(link);
        }
        else
        {
            link.Update(url, r.IsEnabled, r.SortOrder);
        }

        await db.SaveChangesAsync(cancellationToken);
        return ToDto(link);
    }

    public async Task<Result> DeleteSocialAsync(string platformName, CancellationToken cancellationToken)
    {
        if (!Enum.TryParse<SocialPlatform>(platformName, true, out var platform) || !Enum.IsDefined(platform))
        {
            return Error.NotFound(ErrorCodes.Content.NotFound, "Unknown social platform.");
        }

        var link = await db.SocialLinks.FirstOrDefaultAsync(s => s.Platform == platform, cancellationToken);
        if (link is null)
        {
            return Error.NotFound(ErrorCodes.Content.NotFound, "There is no link for this platform.");
        }

        db.SocialLinks.Remove(link);
        await db.SaveChangesAsync(cancellationToken);
        return Result.Success();
    }

    // ---- trust badges (FR-Q07) ----

    public async Task<Result<IReadOnlyList<TrustBadgeDto>>> ListBadgesAsync(CancellationToken cancellationToken)
    {
        var rows = await db.TrustBadges.AsNoTracking().OrderBy(b => b.SortOrder).ThenBy(b => b.Key).ToListAsync(cancellationToken);
        return rows.Select(ToDto).ToList();
    }

    public async Task<Result<TrustBadgeDto>> SaveBadgeAsync(string key, SaveTrustBadgeRequest r, CancellationToken cancellationToken)
    {
        var normalized = TrustBadgeKeys.Normalize(key);
        var badge = normalized is null ? null : await db.TrustBadges.FirstOrDefaultAsync(b => b.Key == normalized, cancellationToken);
        if (badge is null)
        {
            return Error.NotFound(ErrorCodes.Content.NotFound, "Badge not found.");
        }

        var errors = new Dictionary<string, string[]>();
        ContentRules.CheckPlain(errors, "textEn", r.TextEn, ContentRules.BadgeTextMax);
        ContentRules.CheckPlain(errors, "textBn", r.TextBn, ContentRules.BadgeTextMax);
        if (r.IsEnabled && string.IsNullOrWhiteSpace(r.TextEn))
        {
            errors["textEn"] = ["Write the English text before switching the badge on."];
        }

        if (r.SortOrder is < 0 or > 1000)
        {
            errors["sortOrder"] = ["Between 0 and 1000."];
        }

        if (errors.Count > 0)
        {
            return ContentRules.Invalid(errors);
        }

        badge.Update(r.TextEn, r.TextBn, r.IsEnabled, r.SortOrder);
        await db.SaveChangesAsync(cancellationToken);
        return ToDto(badge);
    }
}

/// <summary>What the storefront may read (FR-Q01, Q02, Q03, Q07): published pages, the brand profile, enabled social links and badges. No staff data, no drafts.</summary>
public sealed class ContentPublicService(IApplicationDbContext db)
{
    public async Task<Result<PublicPageDto>> GetPageAsync(string key, CancellationToken cancellationToken)
    {
        var normalized = ContentPageKeys.Normalize(key);
        var page = normalized is null ? null : await db.ContentPages.AsNoTracking().FirstOrDefaultAsync(p => p.Key == normalized && p.IsPublished, cancellationToken);
        if (page is null)
        {
            return Error.NotFound(ErrorCodes.Content.NotFound, "Page not found.");
        }

        return new PublicPageDto(
            page.Key, new(page.TitleEn, page.TitleBn), new(page.BodyEn, page.BodyBn), new(page.MetaTitleEn, page.MetaTitleBn), new(page.MetaDescriptionEn, page.MetaDescriptionBn), page.UpdatedAt ?? page.CreatedAt);
    }

    public async Task<Result<PublicBrandDto>> GetBrandAsync(CancellationToken cancellationToken)
    {
        var b = await db.BrandProfiles.AsNoTracking().OrderBy(x => x.CreatedAt).FirstOrDefaultAsync(cancellationToken);
        if (b is null)
        {
            return Error.NotFound(ErrorCodes.Content.NotFound, "Brand profile not found.");
        }

        return new PublicBrandDto(
            b.BusinessName, b.LogoFileRef is null ? null : "/files/" + b.LogoFileRef.Replace('\\', '/'), new(b.AddressEn, b.AddressBn), b.Phone, b.WhatsApp, b.Email, new(b.WorkingHoursEn, b.WorkingHoursBn), b.Latitude, b.Longitude);
    }

    public async Task<Result<IReadOnlyList<PublicSocialLinkDto>>> GetSocialAsync(CancellationToken cancellationToken)
    {
        var rows = await db.SocialLinks.AsNoTracking().Where(s => s.IsEnabled).OrderBy(s => s.SortOrder).ThenBy(s => s.Platform).ToListAsync(cancellationToken);
        return rows.Select(s => new PublicSocialLinkDto(s.Platform, s.Url, s.SortOrder)).ToList();
    }

    public async Task<Result<IReadOnlyList<PublicTrustBadgeDto>>> GetBadgesAsync(CancellationToken cancellationToken)
    {
        var rows = await db.TrustBadges.AsNoTracking().Where(b => b.IsEnabled).OrderBy(b => b.SortOrder).ThenBy(b => b.Key).ToListAsync(cancellationToken);
        return rows.Select(b => new PublicTrustBadgeDto(b.Key, new(b.TextEn, b.TextBn), b.SortOrder)).ToList();
    }
}
