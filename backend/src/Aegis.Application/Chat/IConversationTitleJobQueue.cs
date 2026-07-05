namespace Aegis.Application.Chat;

public interface IConversationTitleJobQueue
{
    ValueTask EnqueueAsync(ConversationTitleJob job, CancellationToken cancellationToken = default);

    ValueTask<ConversationTitleJob> DequeueAsync(CancellationToken cancellationToken = default);
}
