namespace Aegis.Domain.Entities;

public sealed class Conversation : AuditableEntity
{
    private readonly List<ChatMessage> _messages = [];

    private Conversation()
    {
    }

    public Conversation(string title)
    {
        InitializeAudit();
        Title = title;
    }

    public string Title { get; private set; } = string.Empty;

    public IReadOnlyCollection<ChatMessage> Messages => _messages.AsReadOnly();

    public void Rename(string title)
    {
        Title = title;
        Touch();
    }

    public ChatMessage AddMessage(string role, string content)
    {
        var message = new ChatMessage(Id, role, content);
        _messages.Add(message);
        Touch();

        return message;
    }
}
