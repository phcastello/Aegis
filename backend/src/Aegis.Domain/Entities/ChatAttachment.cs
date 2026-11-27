namespace Aegis.Domain.Entities;

public sealed class ChatAttachment
{
    private ChatAttachment() { }

    public ChatAttachment(Guid messageId, int position, string fileName, string contentType, byte[] data)
    {
        Id = Guid.NewGuid();
        MessageId = messageId;
        Position = position;
        FileName = fileName;
        ContentType = contentType;
        Data = data;
        SizeBytes = data.Length;
    }

    public Guid Id { get; private set; }
    public Guid MessageId { get; private set; }
    public ChatMessage Message { get; private set; } = null!;
    public int Position { get; private set; }
    public string FileName { get; private set; } = "";
    public string ContentType { get; private set; } = "";
    public int SizeBytes { get; private set; }
    public byte[] Data { get; private set; } = [];
}
