using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Aegis.Infrastructure.Persistence;

/// <summary>
/// Explicit transactions can include message persistence, model audit and memory
/// activity in the same commit. A successful SaveChanges alone is insufficient.
/// </summary>
public sealed class ConversationChangeCommitInterceptor : DbTransactionInterceptor
{
    public override void TransactionCommitted(DbTransaction transaction, TransactionEndEventData eventData) =>
        Finish(eventData, committed: true);

    public override Task TransactionCommittedAsync(DbTransaction transaction, TransactionEndEventData eventData,
        CancellationToken cancellationToken = default)
    {
        Finish(eventData, committed: true);
        return Task.CompletedTask;
    }

    public override void TransactionRolledBack(DbTransaction transaction, TransactionEndEventData eventData) =>
        Finish(eventData, committed: false);

    public override Task TransactionRolledBackAsync(DbTransaction transaction, TransactionEndEventData eventData,
        CancellationToken cancellationToken = default)
    {
        Finish(eventData, committed: false);
        return Task.CompletedTask;
    }

    private static void Finish(TransactionEndEventData eventData, bool committed)
    {
        if (eventData.Context is AegisDbContext db)
            db.FinishConversationChanges(eventData.TransactionId, committed);
    }
}
