using FluentValidation;
using Juvo.Application.Abstractions;
using Juvo.Application.Identity;
using Juvo.Application.Ums;
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

        // Identity and access (Step 3).
        services.AddMemoryCache();
        services.AddScoped<IUserStateCache, UserStateCache>();
        services.AddScoped<AccessService>();
        services.AddScoped<IPermissionChecker, PermissionChecker>();
        services.AddScoped<BranchContextResolver>();
        services.AddScoped<SessionIssuer>();
        services.AddScoped<AuthService>();
        services.AddScoped<CustomerAuthService>();

        // User management (module O, section 4A).
        services.AddScoped<ActorContext>();
        services.AddScoped<AccessChanges>();
        services.AddScoped<UsersService>();
        services.AddScoped<RolesService>();
        services.AddScoped<BranchesService>();
        services.AddScoped<AuditQueryService>();

        // No branch scope unless the host provides one (the API registers the per-request scope).
        services.TryAddScoped<IBranchContext>(_ => UnscopedBranchContext.Instance);

        return services;
    }
}
