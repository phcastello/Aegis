namespace Aegis.Domain.Entities;

public sealed class Conversation : AuditableEntity
{
    private readonly List<ChatMessage> _messages = [];

    private Conversation()
    {
    }

    public Conversation(string? title = null)
    {
        InitializeAudit();
        Title = NormalizeTitle(title);
    }

    public string? Title { get; private set; }

    public ICollection<ChatMessage> Messages => _messages;

    public void Rename(string? title)
    {
        Title = NormalizeTitle(title);
        Touch();
    }

    public ChatMessage AddMessage(string role, string content)
    {
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
