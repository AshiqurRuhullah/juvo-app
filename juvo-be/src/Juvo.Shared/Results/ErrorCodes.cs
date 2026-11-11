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
}
