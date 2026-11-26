using Aegis.Application.Memory;
using Aegis.Application.Models;

namespace Aegis.Application.Chat;

public sealed record ChatStreamEvent(
    string Type,
    Guid? TurnId = null,
    Guid? ConversationId = null,
    string? Content = null,
    Guid? MessageId = null,
    Guid? AssistantMessageId = null,
    string? ConversationTitle = null,
    string? TitleSource = null,
    string? Category = null,
    string? State = null,
    string? Message = null,
    MemoryActivitySnapshot? MemoryActivity = null,
    string? VoiceContinuation = null,
    Guid? SegmentId = null,
    int? Sequence = null,
    WebResearchEvidence? WebResearch = null,
    IReadOnlyList<WebResearchSourceCount>? Sources = null,
    Guid? ResearchJobId = null)
{
    public static ChatStreamEvent Ignored(Guid turnId) => new("ignored", turnId);

    public static ChatStreamEvent Conversation(Guid turnId, Guid conversationId) =>
        new("conversation", turnId, ConversationId: conversationId);

    public static ChatStreamEvent Token(Guid turnId, string content) =>
        new("token", turnId, Content: content);

    public static ChatStreamEvent FinalSegment(Guid turnId, Guid conversationId, Guid messageId, Guid segmentId, int sequence, string content) =>
        new("final_segment", turnId, conversationId, Content: content, AssistantMessageId: messageId, SegmentId: segmentId, Sequence: sequence);

    public static ChatStreamEvent ToolStatus(Guid turnId, string category, string state, string message) =>
        new("tool_status", turnId, Category: category, State: state, Message: message);

    public static ChatStreamEvent WebResearchSources(Guid turnId, Guid conversationId, IReadOnlyList<WebResearchSourceCount> sources) =>
        new("web_research_sources", turnId, conversationId, Sources: sources);

    public static ChatStreamEvent Done(
        Guid turnId,
        Guid conversationId,
        Guid messageId,
        string? conversationTitle,
        string? titleSource,
        MemoryActivitySnapshot? memoryActivity = null,
        string? voiceContinuation = null,
        WebResearchEvidence? webResearch = null,
        Guid? researchJobId = null) =>
        new(
            "done",
            turnId,
            conversationId,
            MessageId: messageId,
            AssistantMessageId: messageId,
            ConversationTitle: conversationTitle,
            TitleSource: titleSource,
            MemoryActivity: memoryActivity,
            VoiceContinuation: voiceContinuation,
            WebResearch: webResearch,
            ResearchJobId: researchJobId);
}
