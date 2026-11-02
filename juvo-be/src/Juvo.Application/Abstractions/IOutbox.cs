namespace Juvo.Application.Abstractions;

/// <summary>Adds a message to the transactional outbox. It is saved with the caller's next <c>SaveChanges</c>.</summary>
public interface IOutboxWriter
{
    void Enqueue<TPayload>(string messageType, TPayload payload);
}

public sealed record OutboxMessageContext(Guid MessageId, string MessageType, string Payload, int Attempt, string? CorrelationId);

/// <summary>Handles one kind of outbox message. Throw to trigger a retry with backoff.</summary>
public interface IOutboxMessageHandler
{
    string MessageType { get; }

    Task HandleAsync(OutboxMessageContext context, CancellationToken cancellationToken);
}
