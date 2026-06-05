namespace Aegis.Domain.Entities;

public sealed class LlmRequestAudit : AuditableEntity
{
    private LlmRequestAudit()
    {
    }

    public LlmRequestAudit(
        Guid conversationId,
        Guid userMessageId,
        Guid? assistantMessageId,
        string provider,
        string model,
        bool success,
        long durationMilliseconds,
        string requestPayloadJson,
        int? httpStatusCode,
        string? responseBody,
        string? failureReason,
        string? errorType)
    {
        InitializeAudit();
        ConversationId = conversationId;
        UserMessageId = userMessageId;
        AssistantMessageId = assistantMessageId;
        Provider = NormalizeRequired(provider, nameof(provider));
        Model = NormalizeRequired(model, nameof(model));
        Success = success;
        DurationMilliseconds = Math.Max(0, durationMilliseconds);
        RequestPayloadJson = NormalizeRequired(requestPayloadJson, nameof(requestPayloadJson));
        HttpStatusCode = httpStatusCode;
        ResponseBody = NormalizeOptional(responseBody);
        FailureReason = NormalizeOptional(failureReason);
        ErrorType = NormalizeOptional(errorType);
    }

    public Guid ConversationId { get; private set; }

    public Guid UserMessageId { get; private set; }

    public Guid? AssistantMessageId { get; private set; }

    public string Provider { get; private set; } = string.Empty;

    public string Model { get; private set; } = string.Empty;

    public bool Success { get; private set; }

    public long DurationMilliseconds { get; private set; }

    public string RequestPayloadJson { get; private set; } = string.Empty;

    public int? HttpStatusCode { get; private set; }

    public string? ResponseBody { get; private set; }

    public string? FailureReason { get; private set; }

    public string? ErrorType { get; private set; }

    public Conversation? Conversation { get; private set; }

    public ChatMessage? UserMessage { get; private set; }

    public ChatMessage? AssistantMessage { get; private set; }

    private static string NormalizeRequired(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("Value cannot be empty.", parameterName);
        }

        return value.Trim();
    }

    private static string? NormalizeOptional(string? value)
    {
        return string.IsNullOrWhiteSpace(value)
            ? null
            : value.Trim();
    }
}
