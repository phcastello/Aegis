// Copyright (C) 2026 Pedro Hasson Castello.
// GNU AGPL v3 only; attribution terms: /ADDITIONAL_TERMS.md.
namespace Aegis.Application.Chat;
public sealed record ConversationProactiveActivity(Guid ConversationId, long LastProactiveSequence, long ReadProactiveSequence);
public interface IConversationProactivity
{
    Task<IReadOnlyList<ConversationProactiveActivity>> ActivityAsync(CancellationToken ct);
    Task<IReadOnlyList<ChatMessageResponse>?> MessagesAsync(Guid conversationId, long after, CancellationToken ct);
    Task<bool> MarkReadAsync(Guid conversationId, long sequence, CancellationToken ct);
}
