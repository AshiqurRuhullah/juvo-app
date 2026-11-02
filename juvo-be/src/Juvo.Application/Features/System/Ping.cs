using FluentValidation;
using Juvo.Application.Messaging;
using Juvo.Shared.Results;

namespace Juvo.Application.Features.System;

/// <summary>Smoke-test query that exercises the whole pipeline (validation, logging) end to end.</summary>
public sealed record PingQuery(string? Name) : IQuery<PingResponse>;

public sealed record PingResponse(string Message, DateTimeOffset ServerTime);

internal sealed class PingQueryValidator : AbstractValidator<PingQuery>
{
    public PingQueryValidator()
    {
        RuleFor(x => x.Name).MaximumLength(50);
    }
}

internal sealed class PingQueryHandler(TimeProvider timeProvider) : IRequestHandler<PingQuery, Result<PingResponse>>
{
    public Task<Result<PingResponse>> HandleAsync(PingQuery request, CancellationToken cancellationToken)
    {
        var who = string.IsNullOrWhiteSpace(request.Name) ? "Juvo" : request.Name.Trim();
        Result<PingResponse> result = new PingResponse($"Hello, {who}", timeProvider.GetUtcNow());
        return Task.FromResult(result);
    }
}
