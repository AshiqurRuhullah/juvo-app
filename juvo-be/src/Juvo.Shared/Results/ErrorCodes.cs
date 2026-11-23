namespace Juvo.Shared.Results;

/// <summary>
/// Stable, machine-readable error codes returned in ProblemDetails (<c>code</c> extension).
/// Format: <c>AREA.REASON</c>. Never rename a code once released; add new ones.
/// </summary>
public static class ErrorCodes
{
    public static class General
    {
        public const string Validation = "GENERAL.VALIDATION";
        public const string NotFound = "GENERAL.NOT_FOUND";
        public const string Conflict = "GENERAL.CONFLICT";
        public const string ConcurrencyConflict = "GENERAL.CONCURRENCY_CONFLICT";
        public const string Unexpected = "GENERAL.UNEXPECTED";
        public const string TooManyRequests = "GENERAL.TOO_MANY_REQUESTS";
        public const string BadRequest = "GENERAL.BAD_REQUEST";
        public const string IdempotencyKeyReused = "GENERAL.IDEMPOTENCY_KEY_REUSED";
    }

    public static class Auth
    {
        public const string InvalidCredentials = "AUTH.INVALID_CREDENTIALS";
        public const string Locked = "AUTH.LOCKED";
        public const string TokenExpired = "AUTH.TOKEN_EXPIRED";
        public const string RefreshReused = "AUTH.REFRESH_REUSED";
        public const string Forbidden = "AUTH.FORBIDDEN";
        public const string Unauthorized = "AUTH.UNAUTHORIZED";
        public const string TwoFactorRequired = "AUTH.TWO_FACTOR_REQUIRED";
        public const string TwoFactorSetupRequired = "AUTH.TWO_FACTOR_SETUP_REQUIRED";
        public const string PasswordChangeRequired = "AUTH.PASSWORD_CHANGE_REQUIRED";
        public const string BranchForbidden = "AUTH.BRANCH_FORBIDDEN";
        public const string AccountDisabled = "AUTH.ACCOUNT_DISABLED";
        public const string OtpInvalid = "AUTH.OTP_INVALID";
        public const string OtpExpired = "AUTH.OTP_EXPIRED";
        public const string OtpTooManyAttempts = "AUTH.OTP_TOO_MANY_ATTEMPTS";
        public const string OtpCooldown = "AUTH.OTP_COOLDOWN";
        public const string InvalidEmail = "AUTH.INVALID_EMAIL";
        public const string InvalidMobile = "AUTH.INVALID_MOBILE";
        public const string WeakPassword = "AUTH.WEAK_PASSWORD";
        public const string ResetTokenInvalid = "AUTH.RESET_TOKEN_INVALID";
    }

    public static class Ums
    {
        public const string EmailTaken = "UMS.EMAIL_TAKEN";
        public const string UserNotFound = "UMS.USER_NOT_FOUND";
        public const string RoleNotFound = "UMS.ROLE_NOT_FOUND";
        public const string RoleNameTaken = "UMS.ROLE_NAME_TAKEN";
        public const string RoleSystemProtected = "UMS.ROLE_SYSTEM_PROTECTED";
        public const string RoleInUse = "UMS.ROLE_IN_USE";
        public const string PermissionUnknown = "UMS.PERMISSION_UNKNOWN";
        public const string BranchNotFound = "UMS.BRANCH_NOT_FOUND";
        public const string BranchCodeTaken = "UMS.BRANCH_CODE_TAKEN";
        public const string BranchDefaultProtected = "UMS.BRANCH_DEFAULT_PROTECTED";
        public const string BranchLastActive = "UMS.BRANCH_LAST_ACTIVE";
        public const string BranchInactive = "UMS.BRANCH_INACTIVE";
        public const string AssignmentExists = "UMS.ASSIGNMENT_EXISTS";
        public const string AssignmentNotFound = "UMS.ASSIGNMENT_NOT_FOUND";
        public const string SelfAction = "UMS.SELF_ACTION_FORBIDDEN";
        public const string LastSuperAdmin = "UMS.LAST_SUPER_ADMIN";
        public const string SessionNotFound = "UMS.SESSION_NOT_FOUND";
    }

    public static class Catalog
    {
        public const string CategoryNotFound = "CATALOG.CATEGORY_NOT_FOUND";
        public const string CategoryInUse = "CATALOG.CATEGORY_IN_USE";
        public const string CategoryCycle = "CATALOG.CATEGORY_CYCLE";
        public const string BrandNotFound = "CATALOG.BRAND_NOT_FOUND";
        public const string BrandInUse = "CATALOG.BRAND_IN_USE";
        public const string SizeChartNotFound = "CATALOG.SIZE_CHART_NOT_FOUND";
        public const string ProductNotFound = "CATALOG.PRODUCT_NOT_FOUND";
        public const string VariantNotFound = "CATALOG.VARIANT_NOT_FOUND";
        public const string SlugTaken = "CATALOG.SLUG_TAKEN";
        public const string SkuTaken = "CATALOG.SKU_TAKEN";
        public const string PublishRules = "CATALOG.PUBLISH_RULES";
        public const string ProductArchived = "CATALOG.PRODUCT_ARCHIVED";
        public const string ImageNotFound = "CATALOG.IMAGE_NOT_FOUND";
        public const string ImageInvalid = "CATALOG.IMAGE_INVALID";
    }

    public static class Order
    {
        public const string StockInsufficient = "ORDER.STOCK_INSUFFICIENT";
        public const string InvalidTransition = "ORDER.INVALID_TRANSITION";
        public const string NotFound = "ORDER.NOT_FOUND";
        public const string CartEmpty = "ORDER.CART_EMPTY";
        public const string TotalChanged = "ORDER.TOTAL_CHANGED";
        public const string DuplicateSuspected = "ORDER.DUPLICATE_SUSPECTED";
        public const string CheckoutBlocked = "ORDER.CHECKOUT_BLOCKED";
        public const string OtpRequired = "ORDER.OTP_REQUIRED";
        public const string PaymentMethodUnavailable = "ORDER.PAYMENT_METHOD_UNAVAILABLE";
        public const string CodNotAvailable = "ORDER.COD_NOT_AVAILABLE";
        public const string HeldForReview = "ORDER.HELD_FOR_REVIEW";
        public const string AdvanceRequired = "ORDER.ADVANCE_REQUIRED";
        public const string PaymentPending = "ORDER.PAYMENT_PENDING";
        public const string CannotCancel = "ORDER.CANNOT_CANCEL";
        public const string BranchNotAllowed = "ORDER.BRANCH_NOT_ALLOWED";
        public const string DiscountTooLarge = "ORDER.DISCOUNT_TOO_LARGE";
        public const string NothingToDo = "ORDER.NOTHING_TO_DO";
    }

    public static class Return
    {
        public const string NotFound = "RETURN.NOT_FOUND";
        public const string InvalidTransition = "RETURN.INVALID_TRANSITION";
        public const string OrderNotEligible = "RETURN.ORDER_NOT_ELIGIBLE";
        public const string WindowExpired = "RETURN.WINDOW_EXPIRED";
        public const string QuantityExceeded = "RETURN.QUANTITY_EXCEEDED";
        public const string ItemNotReturnable = "RETURN.ITEM_NOT_RETURNABLE";
        public const string ReasonInvalid = "RETURN.REASON_INVALID";
        public const string AlreadyRefunded = "RETURN.ALREADY_REFUNDED";
        public const string RefundAmountMismatch = "RETURN.REFUND_AMOUNT_MISMATCH";
        public const string NothingToRefund = "RETURN.NOTHING_TO_REFUND";
        public const string WrongKind = "RETURN.WRONG_KIND";
        public const string PaymentNotReceived = "RETURN.PAYMENT_NOT_RECEIVED";
        public const string ReplacementUnavailable = "RETURN.REPLACEMENT_UNAVAILABLE";
        public const string PhotoInvalid = "RETURN.PHOTO_INVALID";
    }

    public static class Fraud
    {
        public const string BlacklistNotFound = "FRAUD.BLACKLIST_NOT_FOUND";
        public const string BlacklistExists = "FRAUD.BLACKLIST_EXISTS";
        public const string NotWaiting = "FRAUD.NOT_WAITING";
        public const string RuleNotFound = "FRAUD.RULE_NOT_FOUND";
    }

    public static class Inventory
    {
        public const string BranchRequired = "INVENTORY.BRANCH_REQUIRED";
        public const string BranchInactive = "INVENTORY.BRANCH_INACTIVE";
        public const string VariantNotFound = "INVENTORY.VARIANT_NOT_FOUND";
        public const string InsufficientStock = "INVENTORY.INSUFFICIENT_STOCK";
        public const string TransferNotFound = "INVENTORY.TRANSFER_NOT_FOUND";
        public const string TransferState = "INVENTORY.TRANSFER_STATE";
        public const string CountNotFound = "INVENTORY.COUNT_NOT_FOUND";
        public const string CountState = "INVENTORY.COUNT_STATE";
        public const string VariantHasStock = "INVENTORY.VARIANT_HAS_STOCK";
    }

    public static class Cart
    {
        public const string NotFound = "CART.NOT_FOUND";
        public const string ItemNotFound = "CART.ITEM_NOT_FOUND";
        public const string VariantUnavailable = "CART.VARIANT_UNAVAILABLE";
        public const string StockInsufficient = "CART.STOCK_INSUFFICIENT";
        public const string TooManyLines = "CART.TOO_MANY_LINES";
        public const string Empty = "CART.EMPTY";
    }

    public static class Coupon
    {
        public const string NotFound = "COUPON.NOT_FOUND";
        public const string Inactive = "COUPON.INACTIVE";
        public const string NotStarted = "COUPON.NOT_STARTED";
        public const string Expired = "COUPON.EXPIRED";
        public const string UsedUp = "COUPON.USED_UP";
        public const string BelowMinimum = "COUPON.BELOW_MINIMUM";
        public const string CodeTaken = "COUPON.CODE_TAKEN";
    }

    public static class Promo
    {
        public const string NotFound = "PROMO.NOT_FOUND";
        public const string MultipleCoupons = "PROMO.MULTIPLE_COUPONS";
        public const string SlugTaken = "PROMO.SLUG_TAKEN";
        public const string Archived = "PROMO.ARCHIVED";
        public const string Incomplete = "PROMO.INCOMPLETE";
        public const string CampaignNotFound = "PROMO.CAMPAIGN_NOT_FOUND";
        public const string TargetNotFound = "PROMO.TARGET_NOT_FOUND";
        public const string UsedUp = "PROMO.USED_UP";
    }

    public static class Preorder
    {
        public const string AwaitingStock = "PREORDER.AWAITING_STOCK";
    }

    public static class Customer
    {
        public const string NotFound = "CUSTOMER.NOT_FOUND";
        public const string AddressNotFound = "CUSTOMER.ADDRESS_NOT_FOUND";
        public const string WishlistFull = "CUSTOMER.WISHLIST_FULL";
        public const string WishlistItemNotFound = "CUSTOMER.WISHLIST_ITEM_NOT_FOUND";
    }

    public static class Delivery
    {
        public const string ZoneNotFound = "DELIVERY.ZONE_NOT_FOUND";
        public const string ZoneCodeTaken = "DELIVERY.ZONE_CODE_TAKEN";
        public const string NotDeliverable = "DELIVERY.NOT_DELIVERABLE";
        public const string Overweight = "DELIVERY.OVERWEIGHT";
        public const string CountryNotFound = "DELIVERY.COUNTRY_NOT_FOUND";
        public const string CountryDisabled = "DELIVERY.COUNTRY_DISABLED";
        public const string CountryCodeTaken = "DELIVERY.COUNTRY_CODE_TAKEN";
        public const string AreaNotFound = "DELIVERY.AREA_NOT_FOUND";
        public const string UpazilaTaken = "DELIVERY.UPAZILA_TAKEN";
    }

    public static class Payment
    {
        public const string NotFound = "PAYMENT.NOT_FOUND";
        public const string InvalidTransition = "PAYMENT.INVALID_TRANSITION";
        public const string MethodUnavailable = "PAYMENT.METHOD_UNAVAILABLE";
        public const string NotConfigured = "PAYMENT.NOT_CONFIGURED";
        public const string OrderNotPayable = "PAYMENT.ORDER_NOT_PAYABLE";
        public const string AlreadyPaid = "PAYMENT.ALREADY_PAID";
        public const string GatewayError = "PAYMENT.GATEWAY_ERROR";
        public const string AmountMismatch = "PAYMENT.AMOUNT_MISMATCH";
        public const string InvalidSignature = "PAYMENT.INVALID_SIGNATURE";
        public const string WebhookExpired = "PAYMENT.WEBHOOK_EXPIRED";
        public const string WebhookMalformed = "PAYMENT.WEBHOOK_MALFORMED";
        public const string ReferenceRequired = "PAYMENT.REFERENCE_REQUIRED";
        public const string ReferenceReused = "PAYMENT.REFERENCE_REUSED";
        public const string NotUnderReview = "PAYMENT.NOT_UNDER_REVIEW";
        public const string ProofInvalid = "PAYMENT.PROOF_INVALID";
        public const string ReturnUrlInvalid = "PAYMENT.RETURN_URL_INVALID";
        public const string NothingToCollect = "PAYMENT.NOTHING_TO_COLLECT";
        public const string NotCod = "PAYMENT.NOT_COD";
        public const string RefundExceedsPaid = "PAYMENT.REFUND_EXCEEDS_PAID";
        public const string RefundNotPending = "PAYMENT.REFUND_NOT_PENDING";
        public const string RefundFailed = "PAYMENT.REFUND_FAILED";
        public const string RefundNotFound = "PAYMENT.REFUND_NOT_FOUND";
        public const string NothingPaid = "PAYMENT.NOTHING_PAID";
        public const string ConcurrentUpdate = "PAYMENT.CONCURRENT_UPDATE";
        public const string SettingInvalid = "PAYMENT.SETTING_INVALID";
    }

    public static class Courier
    {
        public const string NotFound = "COURIER.NOT_FOUND";
        public const string CodeTaken = "COURIER.CODE_TAKEN";
        public const string NotConfigured = "COURIER.NOT_CONFIGURED";
        public const string Disabled = "COURIER.DISABLED";
        public const string UnknownProvider = "COURIER.UNKNOWN_PROVIDER";
        public const string NotServiceable = "COURIER.NOT_SERVICEABLE";
        public const string NoCourierAvailable = "COURIER.NO_COURIER_AVAILABLE";
        public const string CodNotSupported = "COURIER.COD_NOT_SUPPORTED";
        public const string WeightNotAllowed = "COURIER.WEIGHT_NOT_ALLOWED";
        public const string ProviderError = "COURIER.PROVIDER_ERROR";
        public const string InvalidSignature = "COURIER.INVALID_SIGNATURE";
        public const string WebhookExpired = "COURIER.WEBHOOK_EXPIRED";
        public const string WebhookMalformed = "COURIER.WEBHOOK_MALFORMED";
        public const string NotSimulator = "COURIER.NOT_SIMULATOR";
        public const string SettingInvalid = "COURIER.SETTING_INVALID";
    }

    public static class Shipment
    {
        public const string NotFound = "SHIPMENT.NOT_FOUND";
        public const string OrderNotReady = "SHIPMENT.ORDER_NOT_READY";
        public const string AlreadyBooked = "SHIPMENT.ALREADY_BOOKED";
        public const string InvalidTransition = "SHIPMENT.INVALID_TRANSITION";
        public const string CannotCancel = "SHIPMENT.CANNOT_CANCEL";
        public const string NothingToSettle = "SHIPMENT.NOTHING_TO_SETTLE";
        public const string SettlementNotFound = "SHIPMENT.SETTLEMENT_NOT_FOUND";
        public const string SettlementState = "SHIPMENT.SETTLEMENT_STATE";
        public const string AmountInvalid = "SHIPMENT.AMOUNT_INVALID";
        public const string NoteRequired = "SHIPMENT.NOTE_REQUIRED";
        public const string BulkLimit = "SHIPMENT.BULK_LIMIT";
        public const string ParcelInvalid = "SHIPMENT.PARCEL_INVALID";
    }

    public static class Realtime
    {
        public const string Forbidden = "REALTIME.FORBIDDEN";
        public const string TokenInvalid = "REALTIME.TOKEN_INVALID";
        public const string OrderNotFound = "REALTIME.ORDER_NOT_FOUND";
        public const string BranchNotFound = "REALTIME.BRANCH_NOT_FOUND";
    }

    public static class Invoice
    {
        public const string NotFound = "INVOICE.NOT_FOUND";
        public const string OrderNotInvoiceable = "INVOICE.ORDER_NOT_INVOICEABLE";
        public const string CreditNoteNotFound = "INVOICE.CREDIT_NOTE_NOT_FOUND";
        public const string TaxRuleNotFound = "INVOICE.TAX_RULE_NOT_FOUND";
        public const string TaxRuleInvalid = "INVOICE.TAX_RULE_INVALID";
        public const string TaxRuleCodeTaken = "INVOICE.TAX_RULE_CODE_TAKEN";
        public const string TaxRuleUnverified = "INVOICE.TAX_RULE_UNVERIFIED";
        public const string ExclusiveNotSupported = "INVOICE.EXCLUSIVE_NOT_SUPPORTED";
        public const string PdfFailed = "INVOICE.PDF_FAILED";
    }

    public static class Notify
    {
        public const string TemplateNotFound = "NOTIFY.TEMPLATE_NOT_FOUND";
        public const string TemplateInvalid = "NOTIFY.TEMPLATE_INVALID";
        public const string UnknownPlaceholder = "NOTIFY.UNKNOWN_PLACEHOLDER";
        public const string LogNotFound = "NOTIFY.LOG_NOT_FOUND";
        public const string NotResendable = "NOTIFY.NOT_RESENDABLE";
        public const string ChannelDisabled = "NOTIFY.CHANNEL_DISABLED";
        public const string NoConsent = "NOTIFY.NO_CONSENT";
        public const string LanguageInvalid = "NOTIFY.LANGUAGE_INVALID";
    }

    public static class Purchase
    {
        public const string NotFound = "PURCHASE.NOT_FOUND";
        public const string SupplierNotFound = "PURCHASE.SUPPLIER_NOT_FOUND";
        public const string SupplierInactive = "PURCHASE.SUPPLIER_INACTIVE";
        public const string SupplierNameTaken = "PURCHASE.SUPPLIER_NAME_TAKEN";
        public const string InvalidTransition = "PURCHASE.INVALID_TRANSITION";
        public const string VariantNotFound = "PURCHASE.VARIANT_NOT_FOUND";
        public const string OverReceipt = "PURCHASE.OVER_RECEIPT";
        public const string NothingReceived = "PURCHASE.NOTHING_RECEIVED";
        public const string PaymentNotAllowed = "PURCHASE.PAYMENT_NOT_ALLOWED";
        public const string PaymentExceedsDue = "PURCHASE.PAYMENT_EXCEEDS_DUE";
        public const string InvoiceNotAllowed = "PURCHASE.INVOICE_NOT_ALLOWED";
        public const string ReceiptNotFound = "PURCHASE.RECEIPT_NOT_FOUND";
        public const string CostNotAllowed = "PURCHASE.COST_NOT_ALLOWED";
        public const string AttachmentInvalid = "PURCHASE.ATTACHMENT_INVALID";
    }

    public static class Expense
    {
        public const string NotFound = "EXPENSE.NOT_FOUND";
        public const string InvalidTransition = "EXPENSE.INVALID_TRANSITION";
        public const string CategoryNotFound = "EXPENSE.CATEGORY_NOT_FOUND";
        public const string CategoryInactive = "EXPENSE.CATEGORY_INACTIVE";
        public const string CategoryNameTaken = "EXPENSE.CATEGORY_NAME_TAKEN";
        public const string CategoryInUse = "EXPENSE.CATEGORY_IN_USE";
        public const string AccountNotFound = "EXPENSE.ACCOUNT_NOT_FOUND";
        public const string AccountInactive = "EXPENSE.ACCOUNT_INACTIVE";
        public const string AccountBranchMismatch = "EXPENSE.ACCOUNT_BRANCH_MISMATCH";
        public const string NotEditable = "EXPENSE.NOT_EDITABLE";
        public const string SelfApproval = "EXPENSE.SELF_APPROVAL";
        public const string RecurringNotFound = "EXPENSE.RECURRING_NOT_FOUND";
        public const string AttachmentInvalid = "EXPENSE.ATTACHMENT_INVALID";
        public const string TransferInvalid = "EXPENSE.TRANSFER_INVALID";
        public const string InsufficientBalance = "EXPENSE.INSUFFICIENT_BALANCE";
    }

    public static class Ticket
    {
        public const string NotFound = "TICKET.NOT_FOUND";
        public const string InvalidTransition = "TICKET.INVALID_TRANSITION";
        public const string ReopenWindowExpired = "TICKET.REOPEN_WINDOW_EXPIRED";
        public const string RateLimited = "TICKET.RATE_LIMITED";
        public const string OrderNotFound = "TICKET.ORDER_NOT_FOUND";
        public const string OrderMismatch = "TICKET.ORDER_MISMATCH";
        public const string AssigneeNotFound = "TICKET.ASSIGNEE_NOT_FOUND";
        public const string CannedReplyNotFound = "TICKET.CANNED_REPLY_NOT_FOUND";
        public const string AttachmentInvalid = "TICKET.ATTACHMENT_INVALID";
        public const string ClosedTicket = "TICKET.CLOSED";
        public const string BulkLimit = "TICKET.BULK_LIMIT";
    }

    public static class Marketing
    {
        public const string SegmentNotFound = "MARKETING.SEGMENT_NOT_FOUND";
        public const string SegmentInvalid = "MARKETING.SEGMENT_INVALID";
        public const string SegmentInUse = "MARKETING.SEGMENT_IN_USE";
        public const string SegmentBuiltIn = "MARKETING.SEGMENT_BUILT_IN";
        public const string CampaignNotFound = "MARKETING.CAMPAIGN_NOT_FOUND";
        public const string CampaignInvalid = "MARKETING.CAMPAIGN_INVALID";
        public const string CampaignNotEditable = "MARKETING.CAMPAIGN_NOT_EDITABLE";
        public const string CampaignInvalidState = "MARKETING.CAMPAIGN_INVALID_STATE";
        public const string ChannelOff = "MARKETING.CHANNEL_OFF";
        public const string ScheduleInvalid = "MARKETING.SCHEDULE_INVALID";
        public const string TestNoContact = "MARKETING.TEST_NO_CONTACT";
        public const string UnsubscribeTokenInvalid = "MARKETING.UNSUBSCRIBE_TOKEN_INVALID";
        public const string AutomationNotFound = "MARKETING.AUTOMATION_NOT_FOUND";
        public const string AutomationInvalid = "MARKETING.AUTOMATION_INVALID";
        public const string SettingsInvalid = "MARKETING.SETTINGS_INVALID";
        public const string VariantNotFound = "MARKETING.VARIANT_NOT_FOUND";
        public const string VariantInStock = "MARKETING.VARIANT_IN_STOCK";
        public const string ContactRequired = "MARKETING.CONTACT_REQUIRED";
        public const string SubscriptionLimit = "MARKETING.SUBSCRIPTION_LIMIT";
    }

    public static class Report
    {
        public const string Unknown = "REPORT.UNKNOWN";
        public const string RangeInvalid = "REPORT.RANGE_INVALID";
        public const string RangeTooLarge = "REPORT.RANGE_TOO_LARGE";
        public const string FormatInvalid = "REPORT.FORMAT_INVALID";
        public const string CostHidden = "REPORT.COST_HIDDEN";
    }

    public static class Loyalty
    {
        public const string FeatureOff = "LOYALTY.FEATURE_OFF";
        public const string CustomerNotFound = "LOYALTY.CUSTOMER_NOT_FOUND";
        public const string InsufficientPoints = "LOYALTY.INSUFFICIENT_POINTS";
        public const string InsufficientCredit = "LOYALTY.INSUFFICIENT_CREDIT";
        public const string BelowMinimumPoints = "LOYALTY.BELOW_MINIMUM_POINTS";
        public const string TenderExceedsCap = "LOYALTY.TENDER_EXCEEDS_CAP";
        public const string TenderExceedsTotal = "LOYALTY.TENDER_EXCEEDS_TOTAL";
        public const string TenderNeedsSignIn = "LOYALTY.TENDER_NEEDS_SIGN_IN";
        public const string GiftCardInvalid = "LOYALTY.GIFT_CARD_INVALID";
        public const string GiftCardExpired = "LOYALTY.GIFT_CARD_EXPIRED";
        public const string GiftCardInactive = "LOYALTY.GIFT_CARD_INACTIVE";
        public const string GiftCardEmpty = "LOYALTY.GIFT_CARD_EMPTY";
        public const string GiftCardNotFound = "LOYALTY.GIFT_CARD_NOT_FOUND";
        public const string CodeLocked = "LOYALTY.CODE_LOCKED";
        public const string ReferralCodeInvalid = "LOYALTY.REFERRAL_CODE_INVALID";
        public const string ReferralSelf = "LOYALTY.REFERRAL_SELF";
        public const string ReferralAlreadyApplied = "LOYALTY.REFERRAL_ALREADY_APPLIED";
        public const string ReferralNotEligible = "LOYALTY.REFERRAL_NOT_ELIGIBLE";
    }

    public static class Content
    {
        public const string NotFound = "CONTENT.NOT_FOUND";
        public const string Invalid = "CONTENT.INVALID";
        public const string BodyUnsafe = "CONTENT.BODY_UNSAFE";
        public const string PageIncomplete = "CONTENT.PAGE_INCOMPLETE";
        public const string UrlInvalid = "CONTENT.URL_INVALID";
        public const string RateLimited = "CONTENT.RATE_LIMITED";
    }

    public static class Settings
    {
        public const string UnknownKey = "SETTINGS.UNKNOWN_KEY";
        public const string InvalidValue = "SETTINGS.INVALID_VALUE";
        public const string ReadOnly = "SETTINGS.READ_ONLY";
    }

    public static class Platform
    {
        public const string NotSupported = "SYSTEM.NOT_SUPPORTED";
        public const string UnknownIntegration = "SYSTEM.UNKNOWN_INTEGRATION";
        public const string JobNotFound = "SYSTEM.JOB_NOT_FOUND";
        public const string MessageNotFound = "SYSTEM.MESSAGE_NOT_FOUND";
        public const string NotRequeueable = "SYSTEM.NOT_REQUEUEABLE";
        public const string TooMany = "SYSTEM.TOO_MANY";
    }
}
