using Aegis.Domain.Entities;

namespace Aegis.Application.Prompts;

public interface IPromptBuilder
{
    Task<PromptBuildResult> BuildPromptAsync(
        IReadOnlyList<ChatMessage> recentHistory,
        string currentUserMessage,
        string? pendingEmailActionState = null,
        CancellationToken cancellationToken = default);
}
