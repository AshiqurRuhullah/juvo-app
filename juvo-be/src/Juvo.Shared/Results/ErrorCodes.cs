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

    public static class Order
    {
        public const string StockInsufficient = "ORDER.STOCK_INSUFFICIENT";
        public const string InvalidTransition = "ORDER.INVALID_TRANSITION";
    }
}
