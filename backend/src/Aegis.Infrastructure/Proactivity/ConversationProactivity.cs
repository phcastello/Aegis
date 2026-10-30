using Aegis.Application.Chat;
using Aegis.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
namespace Aegis.Infrastructure.Proactivity;
public sealed class ConversationProactivity(AegisDbContext db) : IConversationProactivity
{
    public async Task<IReadOnlyList<ConversationProactiveActivity>> ActivityAsync(CancellationToken ct) =>
        await db.Conversations.AsNoTracking().Where(c => c.DeletedAt == null && c.LastProactiveSequence > 0)
            .Select(c => new ConversationProactiveActivity(c.Id, c.LastProactiveSequence, c.ReadProactiveSequence)).ToArrayAsync(ct);
    public async Task<IReadOnlyList<ChatMessageResponse>?> MessagesAsync(Guid conversationId, long after, CancellationToken ct) {
        if (!await db.Conversations.AnyAsync(c => c.Id == conversationId && c.DeletedAt == null, ct)) return null;
        return await db.ChatMessages.AsNoTracking().Where(m => m.ConversationId == conversationId && m.IsProactive && m.ProactiveSequence > after)
            .OrderBy(m => m.ProactiveSequence).Take(100).Select(m => new ChatMessageResponse(m.Id, m.ConversationId, m.Role, m.Content, m.CreatedAt, m.Model, null, true, m.ProactiveSequence)).ToArrayAsync(ct);
    }
    public async Task<bool> MarkReadAsync(Guid conversationId, long sequence, CancellationToken ct) {
        if (sequence < 0) throw new ArgumentException("Invalid sequence.");
        // A read never touches history ordering and cannot overwrite a newer read on another device.
        if (db.Database.IsNpgsql()) return await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE conversations SET \"ReadProactiveSequence\" = GREATEST(\"ReadProactiveSequence\", LEAST({sequence}, \"LastProactiveSequence\")) WHERE \"Id\" = {conversationId} AND \"DeletedAt\" IS NULL", ct) > 0;
        var conversation = await db.Conversations.SingleOrDefaultAsync(c => c.Id == conversationId && c.DeletedAt == null, ct);
        if (conversation is null) return false;
        conversation.MarkProactiveRead(sequence); await db.SaveChangesAsync(ct); return true;
    }
}
