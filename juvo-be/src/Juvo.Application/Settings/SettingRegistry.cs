using Juvo.Application.Billing;
using Juvo.Application.Integrations;
using Juvo.Application.Loyalty;
using Juvo.Application.Marketing;
using Juvo.Application.Notifications;
using Juvo.Application.Operations;
using Juvo.Application.Orders;
using Juvo.Application.Payments;
using Juvo.Application.Reports;
using Juvo.Application.Returns;
using Juvo.Domain.Loyalty;
using Juvo.Domain.Support;
using Juvo.Shared.Security;

namespace Juvo.Application.Settings;

/// <summary>The groups of the settings centre, in display order.</summary>
public static class SettingGroups
{
    public const string Features = "features";
    public const string Loyalty = "loyalty";
    public const string Payments = "payments";
    public const string Orders = "orders";
    public const string Inventory = "inventory";
    public const string Returns = "returns";
    public const string Business = "business";
    public const string Notifications = "notifications";
    public const string Marketing = "marketing";
    public const string Operations = "operations";
    public const string Reports = "reports";
    public const string Locale = "locale";
    public const string Security = "security";

    public static readonly IReadOnlyList<string> Order =
        [Features, Loyalty, Payments, Orders, Inventory, Returns, Business, Notifications, Marketing, Operations, Reports, Locale, Security];
}

/// <summary>
/// Every runtime setting the settings centre can change (FR-AF01). Defaults here repeat the fall-back each owning module uses when no <c>PlatformSettings</c> row exists
/// (a unit test keeps the two in step for the typed defaults). Settings that are not listed cannot be changed at run time: see docs/step12g-settings-notes.md.
/// </summary>
public static class SettingRegistry
{
    private static readonly string Feature = Permissions.Settings.FeatureEdit;
    private static readonly string Sys = Permissions.Settings.SystemEdit;
    private static readonly string Gateway = Permissions.Payments.GatewayEdit;

    private static readonly IReadOnlyList<SettingDefinition> Definitions = Build();

    private static readonly Dictionary<string, SettingDefinition> ByKey = Definitions.ToDictionary(d => d.Key, StringComparer.Ordinal);

    public static IReadOnlyList<SettingDefinition> All => Definitions;

    public static SettingDefinition? Find(string? key) => key is not null && ByKey.TryGetValue(key, out var d) ? d : null;

    public static bool IsSensitive(string key) => ByKey.TryGetValue(key, out var d) && d.Sensitive;

    private static SettingDefinition Flag(string key, string group, bool on, string en, string bn, string permission, bool restart = false) =>
        new(key, group, SettingType.Bool, on ? "true" : "false", en, bn, permission, RequiresRestart: restart);

    private static SettingDefinition Whole(string key, string group, int value, int min, int max, string en, string bn, string permission = "") =>
        new(key, group, SettingType.Whole, value.ToString(System.Globalization.CultureInfo.InvariantCulture), en, bn, permission.Length == 0 ? Sys : permission, Min: min, Max: max);

    private static SettingDefinition Amount(string key, string group, string value, decimal min, decimal max, string en, string bn, string permission = "") =>
        new(key, group, SettingType.Number, value, en, bn, permission.Length == 0 ? Sys : permission, Min: min, Max: max);

    private static SettingDefinition Choice(string key, string group, string value, IReadOnlyList<string> values, string en, string bn) =>
        new(key, group, SettingType.Choice, value, en, bn, Sys, AllowedValues: values);

    private static SettingDefinition Text(string key, string group, string value, string en, string bn, int maxLength = 500, SettingFormat format = SettingFormat.None, string permission = "") =>
        new(key, group, SettingType.Text, value, en, bn, permission.Length == 0 ? Sys : permission, MaxLength: maxLength, Format: format);

    private static List<SettingDefinition> Build()
    {
        var list = new List<SettingDefinition>
        {
            // ---- features ------------------------------------------------------------------------------
            Flag(LoyaltySettingKeys.Enabled, SettingGroups.Features, false, "Rewards: points, store credit, gift cards and referrals. Off until the shop has set the rules.",
                "রিওয়ার্ড: পয়েন্ট, স্টোর ক্রেডিট, গিফট কার্ড ও রেফারেল। দোকান নিয়ম ঠিক করার আগে বন্ধ থাকে।", Feature),

            // ---- loyalty rules -------------------------------------------------------------------------
            Amount(LoyaltySettingKeys.EarnRatePerBdt, SettingGroups.Loyalty, "0.01", 0m, 1000m, "Points earned for each BDT spent.", "প্রতি টাকা খরচে যত পয়েন্ট মেলে।"),
            Choice(LoyaltySettingKeys.EarnRounding, SettingGroups.Loyalty, "Down", Enum.GetNames<PointsRounding>(), "How earned points are rounded to a whole number.", "অর্জিত পয়েন্ট কীভাবে পূর্ণ সংখ্যায় গোল করা হবে।"),
            Amount(LoyaltySettingKeys.PointValue, SettingGroups.Loyalty, "1", 0.01m, 1000m, "What one point is worth in BDT when spent.", "খরচের সময় এক পয়েন্টের মূল্য (টাকা)।"),
            Whole(LoyaltySettingKeys.MinPointsToRedeem, SettingGroups.Loyalty, 100, 0, 1_000_000, "Fewest points a customer must hold to spend them.", "পয়েন্ট খরচ করতে ন্যূনতম যত পয়েন্ট থাকতে হবে।"),
            Whole(LoyaltySettingKeys.MaxTenderPercent, SettingGroups.Loyalty, 50, 0, 100, "Largest share of an order that points and store credit may pay (percent).", "পয়েন্ট ও স্টোর ক্রেডিট দিয়ে অর্ডারের সর্বোচ্চ যত শতাংশ দেওয়া যাবে।"),
            Whole(LoyaltySettingKeys.PointsExpiryMonths, SettingGroups.Loyalty, 12, 0, 120, "Months until points expire (0 = never).", "কত মাস পরে পয়েন্ট মেয়াদোত্তীর্ণ হবে (০ = কখনো না)।"),
            Whole(LoyaltySettingKeys.ExpiryWarningDays, SettingGroups.Loyalty, 14, 0, 365, "Days before expiry that the customer is warned (0 = no warning).", "মেয়াদ শেষের কত দিন আগে গ্রাহককে জানানো হবে (০ = জানানো হবে না)।"),
            Amount(LoyaltySettingKeys.ReferralRewardReferrer, SettingGroups.Loyalty, "100", 0m, 1_000_000m, "Store credit (BDT) the referrer gets when a friend's first order is delivered.", "বন্ধুর প্রথম অর্ডার পৌঁছালে রেফারার যত টাকা স্টোর ক্রেডিট পাবেন।"),
            Amount(LoyaltySettingKeys.ReferralRewardReferee, SettingGroups.Loyalty, "100", 0m, 1_000_000m, "Store credit (BDT) the invited friend gets after their first delivered order.", "আমন্ত্রিত বন্ধু প্রথম অর্ডার পৌঁছানোর পর যত টাকা স্টোর ক্রেডিট পাবেন।"),
            Whole(LoyaltySettingKeys.ReferralMaxRewards, SettingGroups.Loyalty, 20, 0, 100_000, "Most referral rewards one customer can earn.", "একজন গ্রাহক সর্বোচ্চ কতটি রেফারেল পুরস্কার পেতে পারেন।"),
            Amount(LoyaltySettingKeys.GiftCardMinAmount, SettingGroups.Loyalty, "100", 0.01m, 10_000_000m, "Smallest gift card amount (BDT).", "গিফট কার্ডের সর্বনিম্ন পরিমাণ (টাকা)।"),
            Amount(LoyaltySettingKeys.GiftCardMaxAmount, SettingGroups.Loyalty, "20000", 0.01m, 10_000_000m, "Largest gift card amount (BDT).", "গিফট কার্ডের সর্বোচ্চ পরিমাণ (টাকা)।"),
            Whole(LoyaltySettingKeys.GiftCardValidityMonths, SettingGroups.Loyalty, 12, 0, 120, "Months a new gift card stays valid (0 = no end).", "নতুন গিফট কার্ড কত মাস বৈধ থাকবে (০ = মেয়াদ নেই)।"),

            // ---- payments ------------------------------------------------------------------------------
            Amount(PaymentSettingKeys.CodMaxAmount, SettingGroups.Payments, "0", 0m, 10_000_000m, "Largest order total that may be paid on delivery (BDT, 0 = no limit).", "ডেলিভারিতে নগদ দেওয়া যাবে এমন সর্বোচ্চ অর্ডার মূল্য (টাকা, ০ = সীমা নেই)।", Gateway),
            Text(PaymentSettingKeys.ManualInstructions, SettingGroups.Payments, string.Empty, "Account details customers see for a bank or mobile transfer.", "ব্যাংক বা মোবাইল ট্রান্সফারের জন্য গ্রাহক যে অ্যাকাউন্টের তথ্য দেখেন।", 1000, permission: Gateway),
            Flag(PaymentSettingKeys.AutoConfirmPaid, SettingGroups.Payments, true, "Confirm an online-paid order by itself once the payment is verified.", "অনলাইনে পরিশোধিত অর্ডার যাচাইয়ের পর নিজে থেকে নিশ্চিত হবে।", Gateway),

            // ---- orders and inventory ------------------------------------------------------------------
            Choice(SettingKeys.OtpMode, SettingGroups.Orders, nameof(OtpMode.CodOnly), Enum.GetNames<OtpMode>(), "When a guest must confirm a mobile code before the order is placed: Off, CodOnly or Always.", "অর্ডার দেওয়ার আগে মোবাইল কোড কখন লাগবে: Off, CodOnly বা Always।"),
            Amount(SettingKeys.CodAdvanceThreshold, SettingGroups.Orders, "10000", 0m, 10_000_000m, "Cash-on-delivery orders above this total need the delivery charge paid in advance (BDT).", "এই মূল্যের বেশি ক্যাশ অন ডেলিভারি অর্ডারে ডেলিভারি চার্জ অগ্রিম দিতে হয় (টাকা)।"),
            Whole(SettingKeys.DuplicateWindowMinutes, SettingGroups.Orders, 5, 0, 1440, "Minutes within which the same cart from the same mobile counts as a possible duplicate order (0 = off).", "একই মোবাইল থেকে একই কার্ট যত মিনিটের মধ্যে সম্ভাব্য ডুপ্লিকেট ধরা হবে (০ = বন্ধ)।"),
            Whole(SettingKeys.PaymentHoldMinutes, SettingGroups.Orders, 30, 1, 1440, "Minutes an unpaid online order keeps its stock before it is cancelled.", "অপরিশোধিত অনলাইন অর্ডার বাতিল হওয়ার আগে যত মিনিট স্টক ধরে রাখে।"),
            Choice(SettingKeys.RoutingMode, SettingGroups.Orders, nameof(RoutingMode.AllStock), Enum.GetNames<RoutingMode>(), "Which branch fulfils an online order: DefaultBranch, AllStock, Nearest or Manual.", "অনলাইন অর্ডার কোন শাখা পূরণ করবে: DefaultBranch, AllStock, Nearest বা Manual।"),
            Whole(SettingKeys.MaxManualQuantity, SettingGroups.Orders, 100, 1, 10_000, "Most units of one item staff may put on a manual order.", "ম্যানুয়াল অর্ডারে একটি পণ্যের সর্বোচ্চ যত একক দেওয়া যাবে।"),
            Choice(SettingKeys.DeductAt, SettingGroups.Inventory, nameof(DeductAt.Packing), Enum.GetNames<DeductAt>(), "When stock leaves the shelf: Packing (default) or Delivery. Stock is always reserved at placement.", "স্টক কখন কমবে: Packing (ডিফল্ট) বা Delivery। অর্ডার দেওয়ার সময় স্টক সবসময় সংরক্ষিত হয়।"),

            // ---- returns -------------------------------------------------------------------------------
            Whole(ReturnSettingKeys.WindowDays, SettingGroups.Returns, 7, 0, 365, "Days after delivery in which a return can be requested.", "ডেলিভারির পর যত দিনের মধ্যে ফেরত চাওয়া যাবে।"),
            Amount(ReturnSettingKeys.CustomerFaultReturnCharge, SettingGroups.Returns, "0", 0m, 100_000m, "Courier charge kept from the refund when the customer is at fault (BDT).", "গ্রাহকের দোষে ফেরত হলে রিফান্ড থেকে রাখা কুরিয়ার চার্জ (টাকা)।"),
            Flag(ReturnSettingKeys.RefundDeliveryOnSellerFault, SettingGroups.Returns, true, "Refund the delivery charge when the seller is at fault.", "বিক্রেতার দোষে ফেরত হলে ডেলিভারি চার্জ ফেরত দেওয়া হবে।", Sys),

            // ---- business details on invoices ----------------------------------------------------------
            Text(BusinessSettingKeys.Name, SettingGroups.Business, "Juvo BD", "Seller name printed on invoices.", "ইনভয়েসে ছাপা বিক্রেতার নাম।", 150),
            Text(BusinessSettingKeys.Address, SettingGroups.Business, string.Empty, "Seller address on invoices (empty = the branch address).", "ইনভয়েসে বিক্রেতার ঠিকানা (খালি = শাখার ঠিকানা)।", 500),
            Text(BusinessSettingKeys.Phone, SettingGroups.Business, string.Empty, "Seller phone on invoices (empty = the branch phone).", "ইনভয়েসে বিক্রেতার ফোন (খালি = শাখার ফোন)।", 50),
            Text(BusinessSettingKeys.Email, SettingGroups.Business, string.Empty, "Seller email on invoices (empty = the branch email).", "ইনভয়েসে বিক্রেতার ইমেইল (খালি = শাখার ইমেইল)।", 150, SettingFormat.Email),
            Text(BusinessSettingKeys.TaxId, SettingGroups.Business, string.Empty, "BIN or TIN printed on invoices.", "ইনভয়েসে ছাপা বিআইএন বা টিআইএন।", 50),

            // ---- notifications -------------------------------------------------------------------------
            Flag(NotificationSettingKeys.SmsEnabled, SettingGroups.Notifications, true, "Send SMS messages.", "এসএমএস পাঠানো হবে।", Feature),
            Flag(NotificationSettingKeys.EmailEnabled, SettingGroups.Notifications, true, "Send email messages.", "ইমেইল পাঠানো হবে।", Feature),
            Flag(NotificationSettingKeys.WhatsAppEnabled, SettingGroups.Notifications, false, "Send WhatsApp messages.", "হোয়াটসঅ্যাপ বার্তা পাঠানো হবে।", Feature),

            // ---- locale --------------------------------------------------------------------------------
            Choice(NotificationSettingKeys.DefaultLanguage, SettingGroups.Locale, "en", ["en", "bn"], "Language of messages to a customer who has not chosen one (en or bn).", "যে গ্রাহক ভাষা বাছেননি তাঁকে পাঠানো বার্তার ভাষা (en বা bn)।"),

            // ---- marketing -----------------------------------------------------------------------------
            Flag(MarketingSettingKeys.QuietHoursEnabled, SettingGroups.Marketing, true, "Hold marketing SMS during quiet hours (Asia/Dhaka time).", "নীরব সময়ে মার্কেটিং এসএমএস আটকে রাখা হবে (ঢাকার সময়)।", Sys),
            Text(MarketingSettingKeys.QuietHoursStart, SettingGroups.Marketing, "21:00", "Quiet hours start (HH:mm).", "নীরব সময়ের শুরু (HH:mm)।", 5, SettingFormat.TimeOfDay),
            Text(MarketingSettingKeys.QuietHoursEnd, SettingGroups.Marketing, "09:00", "Quiet hours end (HH:mm).", "নীরব সময়ের শেষ (HH:mm)।", 5, SettingFormat.TimeOfDay),
            Whole(MarketingSettingKeys.FrequencyCapMessages, SettingGroups.Marketing, 3, 0, 1000, "Most marketing messages one customer gets in the cap period (0 = no cap).", "নির্ধারিত সময়ে একজন গ্রাহক সর্বোচ্চ যত মার্কেটিং বার্তা পাবেন (০ = সীমা নেই)।"),
            Whole(MarketingSettingKeys.FrequencyCapDays, SettingGroups.Marketing, 7, 1, 365, "Length of the cap period in days.", "সীমার সময়কাল (দিন)।"),
            Whole(MarketingSettingKeys.ThrottlePerMinute, SettingGroups.Marketing, 600, 1, 100_000, "Most campaign messages sent per minute.", "প্রতি মিনিটে সর্বোচ্চ যত ক্যাম্পেইন বার্তা যাবে।"),
            Whole(MarketingSettingKeys.AttributionWindowDays, SettingGroups.Marketing, 7, 1, 90, "Days after a message in which an order counts as caused by it.", "বার্তার পর যত দিনের মধ্যে অর্ডার সেই বার্তার ফল ধরা হবে।"),
            Text(MarketingSettingKeys.ShopName, SettingGroups.Marketing, "Juvo BD", "Shop name shown in marketing messages.", "মার্কেটিং বার্তায় দেখানো দোকানের নাম।", 100),
            Text(MarketingSettingKeys.LinkUrl, SettingGroups.Marketing, string.Empty, "Shop address used in message links (empty = none).", "বার্তার লিংকে ব্যবহৃত দোকানের ঠিকানা (খালি = নেই)।", 300, SettingFormat.Url),
            Whole(MarketingSettingKeys.MaxNotifyMePerMobile, SettingGroups.Marketing, 10, 1, 1000, "Most 'notify me' requests one mobile number may hold.", "একটি মোবাইল নম্বরে সর্বোচ্চ যত 'জানাও' অনুরোধ থাকতে পারে।"),
            Text(MarketingSettingKeys.SmsOptOutText, SettingGroups.Marketing, MarketingSettings.DefaultSmsOptOut, "Opt-out line added to marketing SMS ({unsubscribeLink} is replaced).", "মার্কেটিং এসএমএসে যোগ হওয়া বন্ধের লাইন ({unsubscribeLink} বদলে যায়)।", 160),

            // ---- operations ----------------------------------------------------------------------------
            Choice(OpsSettingKeys.CostMethod, SettingGroups.Operations, nameof(CostMethod.LastCost), Enum.GetNames<CostMethod>(), "How the cost of a product is worked out when goods are received.", "মাল গ্রহণের সময় পণ্যের খরচ কীভাবে হিসাব হবে।"),
            Amount(OpsSettingKeys.ExpenseApprovalThreshold, SettingGroups.Operations, "0", 0m, 100_000_000m, "Expenses up to this amount are approved automatically (BDT, 0 = every expense waits for approval).", "এই পরিমাণ পর্যন্ত খরচ নিজে থেকে অনুমোদিত হয় (টাকা, ০ = প্রতিটি খরচের অনুমোদন লাগে)।"),
            Whole(OpsSettingKeys.ReopenDays, SettingGroups.Operations, 7, 0, 365, "Days a resolved ticket may be reopened.", "সমাধান হওয়া টিকিট যত দিন পুনরায় খোলা যাবে।"),
            Whole(OpsSettingKeys.AutoCloseDays, SettingGroups.Operations, 7, 1, 365, "Days a resolved ticket waits before it closes by itself.", "সমাধান হওয়া টিকিট নিজে বন্ধ হওয়ার আগে যত দিন অপেক্ষা করে।"),
            Whole(OpsSettingKeys.MaxTicketsPerHour, SettingGroups.Operations, 5, 1, 1000, "Most support tickets one mobile number may open per hour.", "একটি মোবাইল নম্বর ঘণ্টায় সর্বোচ্চ যত সাপোর্ট টিকিট খুলতে পারে।"),

            // ---- public API (FR-AF05) ------------------------------------------------------------------
            Whole(PublicApiSettingKeys.RateLimitPerMinute, SettingGroups.Security, PublicApiSettingKeys.DefaultRateLimitPerMinute, 1, 10_000, "Requests per minute one public API key may make (a key can have its own limit).", "একটি পাবলিক এপিআই কী মিনিটে সর্বোচ্চ যত অনুরোধ করতে পারে (কোনো কীর নিজস্ব সীমা থাকতে পারে)।", Sys),

            // ---- reports -------------------------------------------------------------------------------
            Text(DailySummarySettings.Email, SettingGroups.Reports, string.Empty, "Owner email for the daily summary (empty = no summary is sent).", "দৈনিক সারাংশের জন্য মালিকের ইমেইল (খালি = সারাংশ যাবে না)।", 150, SettingFormat.Email),
            Whole(DailySummarySettings.Hour, SettingGroups.Reports, DailySummarySettings.DefaultHour, 0, 23, "Hour of the day (Asia/Dhaka) the summary is sent.", "দিনের যে ঘণ্টায় সারাংশ যাবে (ঢাকার সময়)।"),
        };

        foreach (var m in PaymentMethodCatalog.All)
        {
            list.Add(Flag(PaymentSettingKeys.Enabled(m.Code), SettingGroups.Payments, m.DefaultEnabled, $"Offer {m.Name} at checkout.", $"চেকআউটে {m.NameBn} দেওয়া হবে।", Gateway));
        }

        foreach (var p in Enum.GetValues<TicketPriority>())
        {
            list.Add(Whole(OpsSettingKeys.SlaResponseHours(p), SettingGroups.Operations, OpsSettingKeys.DefaultResponseHours[p], 1, 8760, $"Hours to a first reply for a {p} ticket.", $"{p} টিকিটে প্রথম উত্তরের সময় (ঘণ্টা)।"));
            list.Add(Whole(OpsSettingKeys.SlaResolutionHours(p), SettingGroups.Operations, OpsSettingKeys.DefaultResolutionHours[p], 1, 8760, $"Hours to resolve a {p} ticket.", $"{p} টিকিট সমাধানের সময় (ঘণ্টা)।"));
        }

        return list;
    }
}
