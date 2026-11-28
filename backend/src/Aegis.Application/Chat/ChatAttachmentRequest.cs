namespace Aegis.Application.Chat;

public sealed record ChatAttachmentRequest(string FileName, string? ContentType, string DataBase64);
public sealed record ChatAttachmentResponse(Guid Id, string FileName, string ContentType, int SizeBytes,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    string? LinkUrl = null);
