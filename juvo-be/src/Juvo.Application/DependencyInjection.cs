using FluentValidation;
using Juvo.Application.Abstractions;
using Juvo.Application.Messaging;
using Juvo.Application.Messaging.Behaviors;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Juvo.Application;

public static class DependencyInjection
{
    /// <summary>Registers the dispatcher, pipeline behaviours (logging, permission, validation, transaction), handlers and validators.</summary>
    public static IServiceCollection AddApplication(this IServiceCollection services)
    {
        var assembly = typeof(DependencyInjection).Assembly;

        services.AddScoped<IDispatcher, Dispatcher>();

        // Order matters: first registered runs outermost.
        services.AddScoped(typeof(IPipelineBehavior<,>), typeof(LoggingBehavior<,>));
        services.AddScoped(typeof(IPipelineBehavior<,>), typeof(PermissionBehavior<,>));
        services.AddScoped(typeof(IPipelineBehavior<,>), typeof(ValidationBehavior<,>));
        services.AddScoped(typeof(IPipelineBehavior<,>), typeof(TransactionBehavior<,>));

        foreach (var type in assembly.GetTypes().Where(t => t is { IsAbstract: false, IsInterface: false }))
        {
            foreach (var contract in type.GetInterfaces().Where(i =>
                         i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IRequestHandler<,>)))
            {
                services.AddScoped(contract, type);
            }
        }

        services.AddValidatorsFromAssembly(assembly, includeInternalTypes: true);

        // Secure default until the Identity module (Step 3) registers the real checker.
        services.TryAddScoped<IPermissionChecker, DenyAllPermissionChecker>();

        return services;
    }

    private sealed class DenyAllPermissionChecker : IPermissionChecker
    {
        public Task<bool> HasPermissionAsync(string permission, CancellationToken cancellationToken = default) =>
            Task.FromResult(false);
    }
}
