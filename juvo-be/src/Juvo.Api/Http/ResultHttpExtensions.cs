using Juvo.Shared.Results;

namespace Juvo.Api.Http;

/// <summary>Maps application <see cref="Result"/> values to HTTP responses (ProblemDetails with a stable error code).</summary>
public static class ResultHttpExtensions
{
    public static IResult ToHttpResult(this Result result, Func<IResult>? onSuccess = null) =>
        result.IsSuccess ? (onSuccess?.Invoke() ?? Results.NoContent()) : ToProblem(result.Error!);

    public static IResult ToHttpResult<T>(this Result<T> result, Func<T, IResult>? onSuccess = null) =>
        result.IsSuccess ? (onSuccess?.Invoke(result.Value) ?? Results.Ok(result.Value)) : ToProblem(result.Error!);

    public static IResult ToProblem(Error error)
    {
        var extensions = new Dictionary<string, object?> { ["code"] = error.Code };
        if (error.Type == ErrorType.Validation)
        {
            return Results.ValidationProblem(
                error.ValidationErrors.ToDictionary(kv => kv.Key, kv => kv.Value),
                detail: error.Message,
                title: "One or more validation errors occurred.",
                extensions: extensions);
        }

        return Results.Problem(
            statusCode: StatusCodeFor(error.Type),
            title: error.Message,
            extensions: extensions);
    }

    public static int StatusCodeFor(ErrorType type) => type switch
    {
        ErrorType.Validation => StatusCodes.Status400BadRequest,
        ErrorType.NotFound => StatusCodes.Status404NotFound,
        ErrorType.Conflict => StatusCodes.Status409Conflict,
        ErrorType.Unauthorized => StatusCodes.Status401Unauthorized,
        ErrorType.Forbidden => StatusCodes.Status403Forbidden,
        ErrorType.TooManyRequests => StatusCodes.Status429TooManyRequests,
        ErrorType.Locked => StatusCodes.Status423Locked,
        _ => StatusCodes.Status422UnprocessableEntity,
    };
}
