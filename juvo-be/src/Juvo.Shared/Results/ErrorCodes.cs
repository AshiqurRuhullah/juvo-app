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
    }

    public static class Order
    {
        public const string StockInsufficient = "ORDER.STOCK_INSUFFICIENT";
        public const string InvalidTransition = "ORDER.INVALID_TRANSITION";
    }
}
