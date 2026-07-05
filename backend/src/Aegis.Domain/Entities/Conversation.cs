namespace Aegis.Domain.Entities;

public sealed class Conversation : AuditableEntity
{
    public const int MaxManualTitleLength = 80;
    public const int MaxGeneratedTitleLength = 50;
    public const string DefaultTitle = "Nova conversa";
    public const string DefaultTitleSource = "default";
    public const string GeneratedTitleSource = "generated";
    public const string ManualTitleSource = "manual";

    private readonly List<ChatMessage> _messages = [];

    private Conversation()
    {
    }

    public Conversation(string? title = null)
    {
        InitializeAudit();
        Title = NormalizeTitle(title);
        TitleSource = DefaultTitleSource;
    }

    public string? Title { get; private set; }

    public string? TitleSource { get; private set; }

    public DateTimeOffset? TitleGeneratedAt { get; private set; }

    public DateTimeOffset? DeletedAt { get; private set; }

    public ICollection<ChatMessage> Messages => _messages;

    public bool CanGenerateAutomaticTitle =>
        DeletedAt is null &&
        TitleGeneratedAt is null &&
        !string.Equals(TitleSource, ManualTitleSource, StringComparison.OrdinalIgnoreCase);

    public void Rename(string title)
    {
        var normalizedTitle = NormalizeTitle(title);
        if (normalizedTitle is null)
        {
            throw new ArgumentException("Title cannot be empty.", nameof(title));
        }

        if (normalizedTitle.Length > MaxManualTitleLength)
        {
            throw new ArgumentException(
                $"Title cannot be longer than {MaxManualTitleLength} characters.",
                nameof(title));
        }

        Title = normalizedTitle;
        TitleSource = ManualTitleSource;
        Touch();
    }

    public void SetGeneratedTitle(string title, DateTimeOffset? now = null)
    {
        if (!CanGenerateAutomaticTitle)
        {
            return;
        }

        var normalizedTitle = NormalizeTitle(title);
        if (normalizedTitle is null)
        {
            return;
        }

        Title = normalizedTitle.Length <= MaxGeneratedTitleLength
            ? normalizedTitle
            : normalizedTitle[..MaxGeneratedTitleLength].TrimEnd();
        TitleSource = GeneratedTitleSource;
        TitleGeneratedAt = now ?? DateTimeOffset.UtcNow;
        Touch();
    }

    public void Delete(DateTimeOffset? now = null)
    {
        if (DeletedAt is not null)
        {
            return;
        }

        DeletedAt = now ?? DateTimeOffset.UtcNow;
        Touch(DeletedAt);
    }

    public ChatMessage AddMessage(string role, string content)
    {
        if (DeletedAt is not null)
        {
            throw new InvalidOperationException("Cannot add messages to a deleted conversation.");
        }

        var message = new ChatMessage(Id, role, content);
        _messages.Add(message);
        Touch();

        return message;
    }

    private static string? NormalizeTitle(string? title)
    {
        return string.IsNullOrWhiteSpace(title)
            ? null
            : title.Trim();
    }
}
