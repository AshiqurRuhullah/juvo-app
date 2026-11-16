namespace Juvo.Shared.Security;

/// <summary>Permission codes of the marketing module (AC). All of them already exist in <see cref="PermissionCatalog"/>.</summary>
public static class MarketingPermissions
{
    public const string SegmentView = "marketing.segment.view";
    public const string SegmentEdit = "marketing.segment.edit";
    public const string CampaignView = "marketing.campaign.view";
    public const string CampaignCreate = "marketing.campaign.create";
    public const string CampaignSend = "marketing.campaign.send";
    public const string AutomationView = "marketing.automation.view";
    public const string AutomationEdit = "marketing.automation.edit";
}
