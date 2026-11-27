namespace Aegis.Domain.Entities;

public sealed class UploadedChatFile
{
    private UploadedChatFile() { }

    public UploadedChatFile(string fileName, string contentType, int sizeBytes, DateTimeOffset expiresAt)
    {
        Id = Guid.NewGuid();
        FileName = fileName;
        ContentType = contentType;
        SizeBytes = sizeBytes;
        ExpiresAt = expiresAt;
    }

    public Guid Id { get; private set; }
    public string FileName { get; private set; } = "";
    public string ContentType { get; private set; } = "";
    public int SizeBytes { get; private set; }
    public DateTimeOffset ExpiresAt { get; private set; }
    public Guid? AttachmentId { get; private set; }
}

public sealed class ChatUploadChunk
{
    private ChatUploadChunk() { }

    public ChatUploadChunk(Guid uploadId, int position, byte[] data)
    {
        UploadId = uploadId;
        Position = position;
        Data = data;
    }

    public Guid UploadId { get; private set; }
    public int Position { get; private set; }
    public byte[] Data { get; private set; } = [];
}
