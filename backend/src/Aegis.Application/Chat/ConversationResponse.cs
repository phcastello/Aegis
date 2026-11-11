// Copyright (C) 2026 Pedro Hasson Castello.
// GNU AGPL v3 only; attribution terms: /ADDITIONAL_TERMS.md.
namespace Aegis.Application.Chat;

public sealed record ConversationResponse(
    Guid Id,
    string? Title,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    string? TitleSource,
    IReadOnlyList<ChatMessageResponse> Messages);
