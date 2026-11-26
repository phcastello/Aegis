namespace Aegis.Domain.Entities;

public sealed class ResearchJob
{
    public const string Queued = "queued";
    public const string Running = "running";
    public const string Completed = "completed";
    public const string Failed = "failed";
    public const string Cancelled = "cancelled";

    private ResearchJob() { }

    public ResearchJob(Guid id, Guid conversationId, Guid userMessageId, DateTimeOffset now)
    {
        Id = id;
        ConversationId = conversationId;
        UserMessageId = userMessageId;
        CreatedAt = UpdatedAt = now;
        State = Phase = Queued;
    }

    public Guid Id { get; private set; }
    public Guid ConversationId { get; private set; }
    public Guid UserMessageId { get; private set; }
    public Guid? ResultMessageId { get; private set; }
    public string State { get; private set; } = string.Empty;
    public string? Phase { get; private set; }
    public string? ErrorCode { get; private set; }
    public string SourcesJson { get; private set; } = "[]";
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }
    public DateTimeOffset? DeadlineAt { get; private set; }
    public int Revision { get; private set; }

    public void Start(DateTimeOffset now, TimeSpan timeout)
    {
        State = Running;
        Phase = null;
        DeadlineAt = now.Add(timeout);
        Touch(now);
    }

    public void Report(string? phase, string sourcesJson, DateTimeOffset now)
    {
        Phase = phase;
        SourcesJson = sourcesJson;
        Touch(now);
    }

    public void Complete(Guid resultMessageId, DateTimeOffset now)
    {
        State = Completed;
        Phase = "finished";
        ResultMessageId = resultMessageId;
        Touch(now);
    }

    public void Fail(string errorCode, DateTimeOffset now)
    {
        State = Failed;
        Phase = null;
        ErrorCode = errorCode;
        Touch(now);
    }

    public void Cancel(DateTimeOffset now)
    {
        State = Cancelled;
        Phase = null;
        Touch(now);
    }

    private void Touch(DateTimeOffset now)
    {
        UpdatedAt = now;
        Revision++;
    }
}
