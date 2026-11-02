using Juvo.Application.Abstractions;
using Juvo.Shared.Results;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Juvo.Api.Http;

internal static class ProblemDetailsSetup
{
    /// <summary>RFC 9457 problem details for every error; adds a stable <c>code</c>, the <c>correlationId</c> and the <c>traceId</c>.</summary>
    public static IServiceCollection AddJuvoProblemDetails(this IServiceCollection services)
    {
        services.AddProblemDetails(options =>
        {
            options.CustomizeProblemDetails = context =>
            {
                var problem = context.ProblemDetails;
                problem.Extensions.TryAdd("code", DefaultCode(problem.Status ?? context.HttpContext.Response.StatusCode));
                problem.Extensions["correlationId"] = CorrelationIdMiddleware.Get(context.HttpContext);
                problem.Extensions["traceId"] = System.Diagnostics.Activity.Current?.Id ?? context.HttpContext.TraceIdentifier;
            };
        });
        services.AddExceptionHandler<GlobalExceptionHandler>();
        return services;
    }

    public static string DefaultCode(int status) => status switch
    {
        400 => ErrorCodes.General.BadRequest,
        401 => ErrorCodes.Auth.Unauthorized,
        403 => ErrorCodes.Auth.Forbidden,
        404 => ErrorCodes.General.NotFound,
        409 => ErrorCodes.General.Conflict,
        429 => ErrorCodes.General.TooManyRequests,
        _ => status >= 500 ? ErrorCodes.General.Unexpected : ErrorCodes.General.BadRequest,
    };
}

/// <summary>Turns unexpected exceptions into problem details. Internal details are only shown in Development.</summary>
internal sealed class GlobalExceptionHandler(
    IProblemDetailsService problemDetails,
    IHostEnvironment environment,
    ILogger<GlobalExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        if (exception is OperationCanceledException && httpContext.RequestAborted.IsCancellationRequested)
        {
            httpContext.Response.StatusCode = 499; // client closed the request
            return true;
        }

        if (exception is BranchAccessDeniedException denied)
        {
            await httpContext.RequestServices.GetRequiredService<IAccessDeniedAuditor>()
                .RecordAsync(denied.Entity, $"write refused in branch {denied.BranchId}", cancellationToken);
        }

        var (status, code, title) = exception switch
        {
            BranchAccessDeniedException => (StatusCodes.Status403Forbidden, ErrorCodes.Auth.BranchForbidden,
                "You do not have access to this branch."),
            DbUpdateConcurrencyException => (StatusCodes.Status409Conflict, ErrorCodes.General.ConcurrencyConflict,
                "The record was changed by someone else. Reload and try again."),
            BadHttpRequestException => (StatusCodes.Status400BadRequest, ErrorCodes.General.BadRequest, "The request is not valid."),
            _ => (StatusCodes.Status500InternalServerError, ErrorCodes.General.Unexpected, "An unexpected error occurred."),
        };

        if (status >= 500)
        {
            logger.LogError(exception, "Unhandled exception for {Method} {Path}", httpContext.Request.Method, httpContext.Request.Path);
        }
        else
        {
            logger.LogWarning(exception, "Handled {Code} for {Method} {Path}", code, httpContext.Request.Method, httpContext.Request.Path);
        }

        httpContext.Response.StatusCode = status;
        var problem = new ProblemDetails
        {
            Status = status,
            Title = title,
            Detail = environment.IsDevelopment() ? exception.ToString() : null,
        };
        problem.Extensions["code"] = code;

        return await problemDetails.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = httpContext,
            ProblemDetails = problem,
            Exception = exception,
        });
    }
}
