using FluentValidation;
using Juvo.Application;
using Juvo.Application.Abstractions;
using Juvo.Application.Features.System;
using Juvo.Application.Messaging;
using Juvo.Shared.Results;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace Juvo.Application.Tests.Messaging;

public class DispatcherTests
{
    // --- test doubles ---------------------------------------------------------------------------------

    private sealed class FakeUser(bool authenticated) : ICurrentUser
    {
        public bool IsAuthenticated => authenticated;
        public Guid? UserId => authenticated ? Guid.NewGuid() : null;
        public string? UserName => authenticated ? "tester" : null;
        public Guid? BranchId => null;
        public string? IpAddress => "127.0.0.1";
        public string? UserAgent => "tests";
        public Guid? SessionId => null;
        public bool IsLimitedToken => false;
        public string? SubjectType => authenticated ? "staff" : null;
        public string? CorrelationId => "corr";
    }

    private sealed class FakeUnitOfWork : IUnitOfWork
    {
        public int Saves { get; private set; }
        public int Transactions { get; private set; }

        public Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
        {
            Saves++;
            return Task.FromResult(1);
        }

        public async Task<T> ExecuteInTransactionAsync<T>(Func<CancellationToken, Task<T>> action, CancellationToken cancellationToken = default)
        {
            Transactions++;
            return await action(cancellationToken);
        }
    }

    private sealed class AllowChecker(bool allow) : IPermissionChecker
    {
        public Task<bool> HasPermissionAsync(string permission, CancellationToken cancellationToken = default) => Task.FromResult(allow);

        public Task<bool> HasPermissionAsync(string permission, PermissionScope scope, CancellationToken cancellationToken = default) => Task.FromResult(allow);

        public Task<bool> HasPermissionInBranchAsync(string permission, Guid branchId, CancellationToken cancellationToken = default) => Task.FromResult(allow);
    }

    private sealed record SecretQuery : IQuery<string>, IRequirePermission
    {
        public string Permission => "reports.finance.view";
    }

    private sealed class SecretHandler : IRequestHandler<SecretQuery, Result<string>>
    {
        public Task<Result<string>> HandleAsync(SecretQuery request, CancellationToken cancellationToken) =>
            Task.FromResult<Result<string>>("top secret");
    }

    private sealed record SaveCommand(bool Succeed) : ICommand<int>;

    private sealed class SaveHandler : IRequestHandler<SaveCommand, Result<int>>
    {
        public Task<Result<int>> HandleAsync(SaveCommand request, CancellationToken cancellationToken) =>
            Task.FromResult<Result<int>>(request.Succeed ? 7 : Error.Conflict());
    }

    private sealed record VoidCommand : ICommand;

    private sealed class VoidHandler : IRequestHandler<VoidCommand, Result>
    {
        public Task<Result> HandleAsync(VoidCommand request, CancellationToken cancellationToken) => Task.FromResult(Result.Success());
    }

    private sealed class VoidValidator : AbstractValidator<VoidCommand>
    {
        public VoidValidator() => RuleFor(_ => _).Must(_ => false).WithMessage("nope").OverridePropertyName("field");
    }

    private static (ServiceProvider Provider, FakeUnitOfWork Uow) Build(bool authenticated = true, bool allow = true)
    {
        var uow = new FakeUnitOfWork();
        var services = new ServiceCollection();
        services.AddSingleton(typeof(Microsoft.Extensions.Logging.ILogger<>), typeof(NullLogger<>));
        services.AddSingleton<TimeProvider>(new FakeTimeProvider(new DateTimeOffset(2026, 11, 2, 12, 0, 0, TimeSpan.Zero)));
        services.AddSingleton<ICurrentUser>(new FakeUser(authenticated));
        services.AddSingleton<IUnitOfWork>(uow);
        services.AddApplication();
        services.AddSingleton<IPermissionChecker>(new AllowChecker(allow));
        services.AddScoped<IRequestHandler<SecretQuery, Result<string>>, SecretHandler>();
        services.AddScoped<IRequestHandler<SaveCommand, Result<int>>, SaveHandler>();
        services.AddScoped<IRequestHandler<VoidCommand, Result>, VoidHandler>();
        services.AddScoped<IValidator<VoidCommand>, VoidValidator>();
        return (services.BuildServiceProvider(), uow);
    }

    private static Task<T> Send<T>(IServiceProvider provider, IRequest<T> request) =>
        provider.GetRequiredService<IDispatcher>().SendAsync(request, TestContext.Current.CancellationToken);

    // --- tests ----------------------------------------------------------------------------------------

    [Fact]
    public async Task Ping_query_runs_through_the_pipeline_and_uses_time_provider()
    {
        var (provider, _) = Build();

        var result = await Send(provider, new PingQuery("Ashiq"));

        Assert.True(result.IsSuccess);
        Assert.Equal("Hello, Ashiq", result.Value.Message);
        Assert.Equal(new DateTimeOffset(2026, 11, 2, 12, 0, 0, TimeSpan.Zero), result.Value.ServerTime);
    }

    [Fact]
    public async Task Validator_failure_becomes_a_validation_error_with_field_messages()
    {
        var (provider, _) = Build();

        var result = await Send(provider, new PingQuery(new string('x', 51)));

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorType.Validation, result.Error!.Type);
        Assert.Equal(ErrorCodes.General.Validation, result.Error.Code);
        Assert.True(result.Error.ValidationErrors.ContainsKey("Name"));
    }

    [Fact]
    public async Task Request_with_permission_is_unauthorized_when_not_signed_in()
    {
        var (provider, _) = Build(authenticated: false);

        var result = await Send(provider, new SecretQuery());

        Assert.Equal(ErrorType.Unauthorized, result.Error!.Type);
    }

    [Fact]
    public async Task Request_with_permission_is_forbidden_when_checker_denies()
    {
        var (provider, _) = Build(allow: false);

        var result = await Send(provider, new SecretQuery());

        Assert.Equal(ErrorType.Forbidden, result.Error!.Type);
    }

    [Fact]
    public async Task Request_with_permission_runs_when_checker_allows()
    {
        var (provider, _) = Build();

        var result = await Send(provider, new SecretQuery());

        Assert.Equal("top secret", result.Value);
    }

    [Fact]
    public async Task Command_runs_in_a_transaction_and_saves_on_success()
    {
        var (provider, uow) = Build();

        var result = await Send(provider, new SaveCommand(Succeed: true));

        Assert.Equal(7, result.Value);
        Assert.Equal(1, uow.Transactions);
        Assert.Equal(1, uow.Saves);
    }

    [Fact]
    public async Task Failed_command_does_not_save()
    {
        var (provider, uow) = Build();

        var result = await Send(provider, new SaveCommand(Succeed: false));

        Assert.True(result.IsFailure);
        Assert.Equal(1, uow.Transactions);
        Assert.Equal(0, uow.Saves);
    }

    [Fact]
    public async Task Query_does_not_open_a_transaction()
    {
        var (provider, uow) = Build();

        await Send(provider, new PingQuery(null));

        Assert.Equal(0, uow.Transactions);
    }

    [Fact]
    public async Task Non_generic_result_commands_are_validated_too()
    {
        var (provider, uow) = Build();

        var result = await Send(provider, new VoidCommand());

        Assert.Equal(ErrorType.Validation, result.Error!.Type);
        Assert.Equal(0, uow.Transactions);
    }

    [Fact]
    public async Task Missing_handler_throws()
    {
        var (provider, _) = Build();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Send(provider, new Unhandled()));
    }

    private sealed record Unhandled : IQuery<int>;
}
