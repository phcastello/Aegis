using Aegis.Application.Chat;
using Microsoft.EntityFrameworkCore;

namespace Aegis.Infrastructure.Persistence;

// ZIP seeks to its directory before reading individual entries. Keep only the
// current database chunk, including when ASP.NET serves a ranged download.
internal sealed class ChatUploadStream(AegisDbContext db, Guid id, int size, CancellationToken requestCancellation) : Stream
{
    private long position;
    private int cachedIndex = -1;
    private byte[] cached = [];

    public override bool CanRead => true;
    public override bool CanSeek => true;
    public override bool CanWrite => false;
    public override long Length => size;
    public override long Position
    {
        get => position;
        set => position = value >= 0 ? value : throw new ArgumentOutOfRangeException(nameof(value));
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        var start = origin switch
        {
            SeekOrigin.Begin => 0L,
            SeekOrigin.Current => position,
            SeekOrigin.End => size,
            _ => throw new ArgumentOutOfRangeException(nameof(origin))
        };
        return Position = checked(start + offset);
    }

    private IQueryable<byte[]> Chunk(int index) => db.ChatUploadChunks.AsNoTracking()
        .Where(chunk => chunk.UploadId == id && chunk.Position == index).Select(chunk => chunk.Data);

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        requestCancellation.ThrowIfCancellationRequested();
        if (position >= size || buffer.IsEmpty) return 0;
        var index = (int)(position / ChatUploadLimits.ChunkSize);
        if (cachedIndex != index) Cache(index, Chunk(index).SingleOrDefault());
        return Copy(buffer);
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        requestCancellation.ThrowIfCancellationRequested();
        cancellationToken.ThrowIfCancellationRequested();
        if (position >= size || buffer.IsEmpty) return 0;
        var index = (int)(position / ChatUploadLimits.ChunkSize);
        if (cachedIndex != index)
            Cache(index, await Chunk(index).SingleOrDefaultAsync(cancellationToken.CanBeCanceled ? cancellationToken : requestCancellation));
        return Copy(buffer.Span);
    }

    private void Cache(int index, byte[]? bytes)
    {
        if (bytes?.Length != Math.Min(ChatUploadLimits.ChunkSize, size - index * ChatUploadLimits.ChunkSize))
            throw new IOException("Um bloco do arquivo está indisponível.");
        cached = bytes;
        cachedIndex = index;
    }

    private int Copy(Span<byte> buffer)
    {
        var offset = (int)(position % ChatUploadLimits.ChunkSize);
        var count = Math.Min(buffer.Length, cached.Length - offset);
        cached.AsSpan(offset, count).CopyTo(buffer);
        position += count;
        return count;
    }

    public override void Flush() { }
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
