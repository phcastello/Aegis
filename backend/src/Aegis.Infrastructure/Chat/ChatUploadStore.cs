using Aegis.Application.Chat;
using Aegis.Domain.Entities;
using Aegis.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Aegis.Infrastructure.Chat;

public enum ChunkWriteResult { Saved, NotFound, Conflict, WrongSize }
public sealed class ChatUploadCapacityException() : Exception("Os envios em andamento ocupam o espaço temporário. Conclua ou descarte um deles e tente novamente.");

public sealed class ChatUploadStore(AegisDbContext db)
{
    private const long MaxPendingBytes = 2 * ChatUploadLimits.MaxMessageBytes;

    public async Task<UploadedChatFile> CreateAsync(string name, string? contentType, int size, CancellationToken ct)
    {
        name = name.Trim();
        if (name.Length is 0 or > 255 || name is "." or ".." || name.IndexOfAny(['/', '\\']) >= 0 || name.Any(char.IsControl))
            throw new ArgumentException("Nome de arquivo inválido.");
        if (size is <= 0 or > ChatUploadLimits.MaxFileBytes)
            throw new ArgumentException("O arquivo deve ter entre um byte e 512 MiB.");
        // The model reader identifies the content independently. The client hint
        // is never allowed to introduce parameters or invalid response headers.
        var mime = System.Net.Http.Headers.MediaTypeHeaderValue.TryParse(contentType, out var parsed)
            && parsed.MediaType is { Length: <= 128 } mediaType ? mediaType : "application/octet-stream";
        // Serialize only the short reservation step, across API processes. This
        // bounds temporary disk usage even when several large drafts start together.
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(1819045749, 1633971571)", ct);
        var now = DateTimeOffset.UtcNow;
        var reserved = await db.ChatUploads.Where(u => u.AttachmentId == null)
            .GroupBy(_ => 1).Select(g => new { Count = g.Count(), Bytes = g.Sum(u => (long)u.SizeBytes) })
            .SingleOrDefaultAsync(ct);
        if (reserved is not null && (reserved.Count >= 256 || reserved.Bytes + size > MaxPendingBytes))
            throw new ChatUploadCapacityException();
        var upload = new UploadedChatFile(name, mime, size, now.AddDays(1));
        db.ChatUploads.Add(upload);
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return upload;
    }

    public async Task<ChunkWriteResult> WriteChunkAsync(Guid id, int index, Stream body, long? contentLength, CancellationToken ct)
    {
        var pending = await db.ChatUploads.AsNoTracking()
            .Where(u => u.Id == id && u.AttachmentId == null && u.ExpiresAt > DateTimeOffset.UtcNow)
            .Select(u => new { u.SizeBytes }).SingleOrDefaultAsync(ct);
        if (pending is null) return ChunkWriteResult.NotFound;
        if (index < 0 || index >= ChatUploadLimits.ChunkCount(pending.SizeBytes)) return ChunkWriteResult.WrongSize;
        var expected = Math.Min(ChatUploadLimits.ChunkSize, pending.SizeBytes - index * ChatUploadLimits.ChunkSize);
        if (contentLength is not null && contentLength != expected) return ChunkWriteResult.WrongSize;
        var bytes = new byte[expected];
        try { await body.ReadExactlyAsync(bytes, ct); }
        catch (EndOfStreamException) { return ChunkWriteResult.WrongSize; }
        if (!contentLength.HasValue && await body.ReadAsync(new byte[1], ct) != 0) return ChunkWriteResult.WrongSize;

        // No transaction or database connection is held while the client transmits.
        // Immutable numbered chunks let an interrupted client retry just one PUT.
        var inserted = await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO chat_upload_chunks ("UploadId", "Position", "Data")
            SELECT u."Id", {index}, {bytes} FROM
                (SELECT "Id" FROM chat_uploads WHERE "Id" = {id} AND "AttachmentId" IS NULL
                 AND "ExpiresAt" > now() FOR SHARE) AS u
            ON CONFLICT ("UploadId", "Position") DO NOTHING
            """, ct);
        if (inserted == 1) return ChunkWriteResult.Saved;
        var stored = await db.ChatUploadChunks.AsNoTracking().Where(c => c.UploadId == id && c.Position == index)
            .Select(c => c.Data).SingleOrDefaultAsync(ct);
        if (stored is null) return ChunkWriteResult.NotFound;
        return bytes.AsSpan().SequenceEqual(stored) ? ChunkWriteResult.Saved : ChunkWriteResult.Conflict;
    }

    public Task<int> DiscardAsync(Guid id, CancellationToken ct) =>
        db.ChatUploads.Where(u => u.Id == id && u.AttachmentId == null).ExecuteDeleteAsync(ct);
}
