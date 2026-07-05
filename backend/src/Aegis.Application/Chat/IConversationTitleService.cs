namespace Aegis.Application.Chat;

public interface IConversationTitleService
{
    Task GenerateTitleAsync(ConversationTitleJob job, CancellationToken cancellationToken = default);
}
