namespace Aegis.Application.Feedback;

public sealed class MessageFeedbackTargetNotFoundException(Guid messageId)
    : Exception($"Message '{messageId}' was not found.")
{
    public Guid MessageId { get; } = messageId;
}
