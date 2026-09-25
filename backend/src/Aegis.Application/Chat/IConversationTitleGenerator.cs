namespace Aegis.Application.Chat;

public interface IConversationTitleGenerator
{
    Task<string?> GenerateAsync(
        string userContent,
        string assistantContent,
        CancellationToken cancellationToken = default);
}
