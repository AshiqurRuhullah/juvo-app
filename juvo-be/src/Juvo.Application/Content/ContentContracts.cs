using Juvo.Domain.Content;

namespace Juvo.Application.Content;

/// <summary>A text in both shop languages. Either side can be empty; the shop falls back to English.</summary>
public sealed record LocalizedText(string? En, string? Bn);

// ---- staff ----

public sealed record PageDto(
    string Key, string? TitleEn, string? TitleBn, string? BodyEn, string? BodyBn, string? MetaTitleEn, string? MetaTitleBn, string? MetaDescriptionEn, string? MetaDescriptionBn,
    bool IsPublished, bool CanPublish, DateTimeOffset? PublishedAt, DateTimeOffset UpdatedAt);

public sealed record UpdatePageRequest(
    string? TitleEn, string? TitleBn, string? BodyEn, string? BodyBn, string? MetaTitleEn, string? MetaTitleBn, string? MetaDescriptionEn, string? MetaDescriptionBn);

public sealed record BrandDto(
    string BusinessName, string? LogoFileRef, string? AddressEn, string? AddressBn, string? Phone, string? WhatsApp, string? Email, string? WorkingHoursEn, string? WorkingHoursBn,
    decimal? Latitude, decimal? Longitude, DateTimeOffset UpdatedAt);

public sealed record UpdateBrandRequest(
    string? BusinessName, string? LogoFileRef, string? AddressEn, string? AddressBn, string? Phone, string? WhatsApp, string? Email, string? WorkingHoursEn, string? WorkingHoursBn,
    decimal? Latitude, decimal? Longitude);

public sealed record SocialLinkDto(SocialPlatform Platform, string Url, bool IsEnabled, int SortOrder, DateTimeOffset UpdatedAt);

public sealed record SaveSocialLinkRequest(string? Url, bool IsEnabled, int SortOrder);

public sealed record TrustBadgeDto(string Key, string? TextEn, string? TextBn, bool IsEnabled, int SortOrder, DateTimeOffset UpdatedAt);

public sealed record SaveTrustBadgeRequest(string? TextEn, string? TextBn, bool IsEnabled, int SortOrder);

// ---- public (storefront) ----

public sealed record PublicPageDto(string Key, LocalizedText Title, LocalizedText Body, LocalizedText MetaTitle, LocalizedText MetaDescription, DateTimeOffset UpdatedAt);

public sealed record PublicBrandDto(
    string BusinessName, string? LogoUrl, LocalizedText Address, string? Phone, string? WhatsApp, string? Email, LocalizedText WorkingHours, decimal? Latitude, decimal? Longitude);

public sealed record PublicSocialLinkDto(SocialPlatform Platform, string Url, int SortOrder);

public sealed record PublicTrustBadgeDto(string Key, LocalizedText Text, int SortOrder);
