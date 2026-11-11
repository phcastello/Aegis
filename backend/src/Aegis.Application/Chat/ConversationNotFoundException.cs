// Copyright (C) 2026 Pedro Hasson Castello.
// GNU AGPL v3 only; attribution terms: /ADDITIONAL_TERMS.md.
namespace Aegis.Application.Chat;

public sealed class ConversationNotFoundException(Guid conversationId)
    : Exception($"Conversation '{conversationId}' was not found.")
{
    public Guid ConversationId { get; } = conversationId;
}
