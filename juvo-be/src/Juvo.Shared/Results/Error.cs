namespace Juvo.Shared.Results;

/// <summary>An expected failure: a stable code, a human message and a category.</summary>
public sealed record Error
{
    private static readonly IReadOnlyDictionary<string, string[]> NoDetails =
        new Dictionary<string, string[]>();

    public Error(string code, string message, ErrorType type = ErrorType.Failure,
        IReadOnlyDictionary<string, string[]>? validationErrors = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        Code = code;
        Message = message;
        Type = type;
        ValidationErrors = validationErrors ?? NoDetails;
    }

    public string Code { get; }

    public string Message { get; }

    public ErrorType Type { get; }

    /// <summary>Field name to messages. Only filled for <see cref="ErrorType.Validation"/>.</summary>
    public IReadOnlyDictionary<string, string[]> ValidationErrors { get; }

    public static Error Validation(IReadOnlyDictionary<string, string[]> errors, string message = "One or more validation errors occurred.") =>
        new(ErrorCodes.General.Validation, message, ErrorType.Validation, errors);

    public static Error NotFound(string code = ErrorCodes.General.NotFound, string message = "The requested resource was not found.") =>
        new(code, message, ErrorType.NotFound);

    public static Error Conflict(string code = ErrorCodes.General.Conflict, string message = "The request conflicts with the current state.") =>
        new(code, message, ErrorType.Conflict);

    public static Error Unauthorized(string code = ErrorCodes.Auth.Unauthorized, string message = "Authentication is required.") =>
        new(code, message, ErrorType.Unauthorized);

    public static Error Forbidden(string code = ErrorCodes.Auth.Forbidden, string message = "You do not have permission to do this.") =>
        new(code, message, ErrorType.Forbidden);

    public static Error Failure(string code, string message) => new(code, message, ErrorType.Failure);
}
