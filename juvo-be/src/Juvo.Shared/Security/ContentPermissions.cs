namespace Juvo.Shared.Security;

/// <summary>Permission codes of the site content module (Q). All of them exist in <see cref="PermissionCatalog"/>.</summary>
public static class ContentPermissions
{
    public const string PageView = "content.page.view";
    public const string PageEdit = "content.page.edit";
    public const string PagePublish = "content.page.publish";
    public const string BrandView = "content.brand.view";
    public const string BrandEdit = "content.brand.edit";
    public const string SocialEdit = "content.social.edit";
    public const string BadgeEdit = "content.badge.edit";
}
