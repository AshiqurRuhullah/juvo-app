namespace Juvo.Domain.Common;

/// <summary>
/// Anything the audit interceptor stamps with created/updated time and user. <see cref="Entity"/> implements it;
/// the staff user (which derives from the Identity user type) implements it too.
/// </summary>
public interface IAuditable
{
    Guid Id { get; }

    void StampCreated(DateTimeOffset now, Guid? userId);

    void StampUpdated(DateTimeOffset now, Guid? userId);
}
