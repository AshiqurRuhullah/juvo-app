using Juvo.Shared.Results;

namespace Juvo.Application.Messaging;

/// <summary>A request that returns <typeparamref name="TResponse"/>. All handlers return a <see cref="Result"/>.</summary>
public interface IRequest<out TResponse>
{
}

/// <summary>Runs inside a database transaction (see TransactionBehavior).</summary>
public interface ITransactional
{
}

public interface ICommand : IRequest<Result>, ITransactional
{
}

public interface ICommand<TResponse> : IRequest<Result<TResponse>>, ITransactional
{
}

public interface IQuery<TResponse> : IRequest<Result<TResponse>>
{
}

public interface IRequestHandler<in TRequest, TResponse>
    where TRequest : IRequest<TResponse>
{
    Task<TResponse> HandleAsync(TRequest request, CancellationToken cancellationToken);
}

public delegate Task<TResponse> RequestHandlerDelegate<TResponse>();

/// <summary>Cross-cutting step around a handler: logging, permission, validation, transaction.</summary>
public interface IPipelineBehavior<in TRequest, TResponse>
    where TRequest : IRequest<TResponse>
{
    Task<TResponse> HandleAsync(TRequest request, RequestHandlerDelegate<TResponse> next, CancellationToken cancellationToken);
}

public interface IDispatcher
{
    Task<TResponse> SendAsync<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default);
}
