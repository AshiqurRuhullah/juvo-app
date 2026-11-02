namespace Juvo.Shared.Results;

/// <summary>Category of an expected failure. The API layer maps this to an HTTP status code.</summary>
public enum ErrorType
{
    Failure = 0,
    Validation = 1,
    NotFound = 2,
    Conflict = 3,
    Unauthorized = 4,
    Forbidden = 5,
    TooManyRequests = 6,
    Locked = 7,
}
