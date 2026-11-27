using Aegis.Application.Llm;
using Aegis.Application.Models;
using System.Text.Json.Serialization;

namespace Aegis.Infrastructure.Chat;

public sealed record CreateResearchJobRequest(Guid JobId, Guid? ConversationId, string? Content);

public sealed record ResearchJobSnapshot(
    Guid Id, Guid ConversationId, Guid UserMessageId, string State, string? Phase,
    DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, Guid? ResultMessageId,
    string? ErrorCode, IReadOnlyList<WebResearchSourceCount> Sources,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Kind = null);

internal sealed record ResearchJobInput(
    Guid JobId, Guid ConversationId, Guid UserMessageId, string Content, DateTimeOffset DeadlineAt,
    bool IsHandsFree, bool IsPython = false);

internal sealed record ResearchJobResult(
    string Content, string Model, string? MetadataJson, LlmRequestAuditData Audit);

public sealed record ResearchVoiceResult(Guid ConversationId, string? Text);
