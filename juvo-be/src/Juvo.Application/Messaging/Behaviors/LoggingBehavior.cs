using System.Diagnostics;
using Juvo.Shared.Results;
using Microsoft.Extensions.Logging;

namespace Juvo.Application.Messaging.Behaviors;

/// <summary>Logs every request with its duration and, for failed results, the error code. Never logs request bodies (PII).</summary>
internal sealed class LoggingBehavior<TRequest, TResponse>(ILogger<LoggingBehavior<TRequest, TResponse>> logger)
    : IPipelineBehavior<TRequest, TResponse>
    where TRequest : IRequest<TResponse>
{
    public async Task<TResponse> HandleAsync(TRequest request, RequestHandlerDelegate<TResponse> next, CancellationToken cancellationToken)
    {
        var name = typeof(TRequest).Name;
        var started = Stopwatch.GetTimestamp();
        try
        {
            var response = await next();
            var elapsed = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            if (response is Result { IsFailure: true } failed)
            {
                logger.LogInformation("{Request} failed with {ErrorCode} in {Elapsed:F0} ms", name, failed.Error!.Code, elapsed);
            }
            else
            {
                logger.LogInformation("{Request} succeeded in {Elapsed:F0} ms", name, elapsed);
            }

            return response;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "{Request} threw after {Elapsed:F0} ms", name, Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            throw;
        }
    }
}
