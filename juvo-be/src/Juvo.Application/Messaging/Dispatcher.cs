using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;

namespace Juvo.Application.Messaging;

/// <summary>
/// Small in-process dispatcher (no third-party mediator, so no licence risk). Resolves the handler and the
/// pipeline behaviours from DI; behaviours registered first run outermost.
/// </summary>
internal sealed class Dispatcher(IServiceProvider serviceProvider) : IDispatcher
{
    private static readonly ConcurrentDictionary<Type, object> Wrappers = new();

    public Task<TResponse> SendAsync<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var wrapper = (RequestWrapper<TResponse>)Wrappers.GetOrAdd(request.GetType(), static requestType =>
            Activator.CreateInstance(typeof(RequestWrapper<,>).MakeGenericType(requestType, typeof(TResponse)))!);
        return wrapper.HandleAsync(request, serviceProvider, cancellationToken);
    }

    private abstract class RequestWrapper<TResponse>
    {
        public abstract Task<TResponse> HandleAsync(IRequest<TResponse> request, IServiceProvider provider, CancellationToken cancellationToken);
    }

    private sealed class RequestWrapper<TRequest, TResponse> : RequestWrapper<TResponse>
        where TRequest : IRequest<TResponse>
    {
        public override Task<TResponse> HandleAsync(IRequest<TResponse> request, IServiceProvider provider, CancellationToken cancellationToken)
        {
            var typed = (TRequest)request;
            var handler = provider.GetRequiredService<IRequestHandler<TRequest, TResponse>>();
            RequestHandlerDelegate<TResponse> pipeline = () => handler.HandleAsync(typed, cancellationToken);

            foreach (var behavior in provider.GetServices<IPipelineBehavior<TRequest, TResponse>>().Reverse())
            {
                var next = pipeline;
                var current = behavior;
                pipeline = () => current.HandleAsync(typed, next, cancellationToken);
            }

            return pipeline();
        }
    }
}
