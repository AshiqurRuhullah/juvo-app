using Juvo.Application.Abstractions;
using Juvo.Shared.Results;

namespace Juvo.Application.Messaging.Behaviors;

/// <summary>
/// Wraps commands in one database transaction (inside the retry strategy). Changes are saved only when the
/// handler returns a successful <see cref="Result"/>; a failed result saves nothing, so partial work is never stored.
/// </summary>
internal sealed class TransactionBehavior<TRequest, TResponse>(IUnitOfWork unitOfWork)
    : IPipelineBehavior<TRequest, TResponse>
    where TRequest : IRequest<TResponse>, ITransactional
    where TResponse : Result
{
    public Task<TResponse> HandleAsync(TRequest request, RequestHandlerDelegate<TResponse> next, CancellationToken cancellationToken) =>
        unitOfWork.ExecuteInTransactionAsync(async ct =>
        {
            var response = await next();
            if (response.IsSuccess)
            {
                await unitOfWork.SaveChangesAsync(ct);
            }

            return response;
        }, cancellationToken);
}
