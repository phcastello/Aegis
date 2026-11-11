// Copyright (C) 2026 Pedro Hasson Castello.
// GNU AGPL v3 only; attribution terms: /ADDITIONAL_TERMS.md.
using Aegis.Application.Memory;

namespace Aegis.Application.Chat;

public sealed record ChatMessageResponse(
    Guid Id,
    Guid ConversationId,
    string Role,
    string Content,
    DateTimeOffset CreatedAt,
    string? Model,
    MemoryActivitySnapshot? MemoryActivity = null,
    bool IsProactive = false, long? ProactiveSequence = null);
