using Aegis.Application.Common;
using Aegis.Domain;

namespace Aegis.Application.Tools;

public static class PendingActionGuard
{
    // Intent belongs to the model. Only persisted message identity and ordering are checked here.
    public static async Task<bool> HasValidUserMessageAsync(IAegisDbContext db, ToolExecutionContext context,
        DateTimeOffset? preparedAt = null, CancellationToken cancellationToken = default)
    {
        var message = await db.GetChatMessageAsync(context.UserMessageId, cancellationToken);
        return message is not null && message.ConversationId == context.ConversationId && message.Role == ChatRoles.User &&
            (preparedAt is null || preparedAt < message.CreatedAt);
    }
}
