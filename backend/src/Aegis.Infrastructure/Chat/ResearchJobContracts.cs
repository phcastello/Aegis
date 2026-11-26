using Aegis.Application.Llm;
using Aegis.Application.Models;

namespace Aegis.Infrastructure.Chat;

public sealed record CreateResearchJobRequest(Guid JobId, Guid? ConversationId, string? Content);

public sealed record ResearchJobSnapshot(
    Guid Id, Guid ConversationId, Guid UserMessageId, string State, string? Phase,
    DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, Guid? ResultMessageId,
    string? ErrorCode, IReadOnlyList<WebResearchSourceCount> Sources);

internal sealed record ResearchJobInput(
    Guid JobId, Guid ConversationId, Guid UserMessageId, string Content, DateTimeOffset DeadlineAt);

internal sealed record ResearchJobResult(
    string Content, string Model, string? MetadataJson, LlmRequestAuditData Audit);
