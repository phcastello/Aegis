namespace Aegis.Application.Chat;

public static class ChatUploadLimits
{
    public const int MaxFiles = 4;
    public const int MaxFileBytes = 512 * 1024 * 1024;
    public const int ChunkSize = 4 * 1024 * 1024;

    public static int ChunkCount(int size) => (size - 1) / ChunkSize + 1;
}
