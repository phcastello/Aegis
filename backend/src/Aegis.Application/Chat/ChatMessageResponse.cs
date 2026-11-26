using Aegis.Application.Memory;
using Aegis.Application.Models;

namespace Aegis.Application.Chat;

public sealed record ChatMessageResponse(
    Guid Id,
    Guid ConversationId,
    string Role,
    string Content,
    DateTimeOffset CreatedAt,
    string? Model,
    MemoryActivitySnapshot? MemoryActivity = null,
    bool IsProactive = false, long? ProactiveSequence = null,
    WebResearchEvidence? WebResearch = null);
