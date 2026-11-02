using Juvo.Application.Abstractions;
using Microsoft.EntityFrameworkCore;

namespace Juvo.Infrastructure.Persistence;

internal sealed class UnitOfWork(JuvoDbContext db) : IUnitOfWork
{
    public Task<int> SaveChangesAsync(CancellationToken cancellationToken = default) =>
        db.SaveChangesAsync(cancellationToken);

    public async Task<T> ExecuteInTransactionAsync<T>(Func<CancellationToken, Task<T>> action, CancellationToken cancellationToken = default)
    {
        if (db.Database.CurrentTransaction is not null)
        {
            return await action(cancellationToken);
        }

        // The retry strategy needs the whole unit of work wrapped so a transient failure replays it as one piece.
        var strategy = db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async ct =>
        {
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            var result = await action(ct);
            await transaction.CommitAsync(ct);
            return result;
        }, cancellationToken);
    }
}
