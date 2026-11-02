using Juvo.Application.Abstractions;
using Juvo.Shared.Results;

namespace Juvo.Application.Messaging.Behaviors;

/// <summary>Rejects requests that declare a required permission the current user does not hold.</summary>
internal sealed class PermissionBehavior<TRequest, TResponse>(ICurrentUser currentUser, IPermissionChecker checker)
    : IPipelineBehavior<TRequest, TResponse>
    where TRequest : IRequest<TResponse>, IRequirePermission
    where TResponse : Result
{
    public async Task<TResponse> HandleAsync(TRequest request, RequestHandlerDelegate<TResponse> next, CancellationToken cancellationToken)
    {
        if (!currentUser.IsAuthenticated)
        {
            return ResultFactory<TResponse>.Failure(Error.Unauthorized());
        }

        if (!await checker.HasPermissionAsync(request.Permission, request.Scope, cancellationToken))
        {
            return ResultFactory<TResponse>.Failure(Error.Forbidden());
        }

        return await next();
    }
}
