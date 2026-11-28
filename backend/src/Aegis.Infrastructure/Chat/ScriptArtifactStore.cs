using Aegis.Application.Chat;
using Aegis.Application.Tools;
using Aegis.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Aegis.Infrastructure.Chat;

internal sealed class ScriptArtifactStore(IServiceScopeFactory scopes, ILogger<ScriptArtifactStore> logger) : IScriptArtifactStore
{
    public async Task<IReadOnlyDictionary<string, Guid>> StageAsync(
        IReadOnlyList<ScriptArtifactFile> files, ToolExecutionContext context, CancellationToken ct)
    {
        var staged = new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase);
        if (files.Count == 0) return staged;
        context.Artifacts.CheckFileCount(files.Select(file => file.FileName));
        // Upload reservations commit separately, without flushing the in-progress chat or job.
        await using var scope = scopes.CreateAsyncScope();
        var uploads = scope.ServiceProvider.GetRequiredService<ChatUploadStore>();
        foreach (var file in files)
        {
            if (staged.ContainsKey(file.FileName))
                throw new ArgumentException("Os arquivos de saída precisam ter nomes distintos.");
            await using var stream = new FileStream(file.FullPath, FileMode.Open, FileAccess.Read, FileShare.Read,
                65536, FileOptions.Asynchronous | FileOptions.SequentialScan);
            if (stream.Length > ChatUploadLimits.MaxFileBytes)
                throw new ArgumentException("O arquivo gerado excede 512 MiB.");
            var size = (int)stream.Length;
            var sample = new byte[Math.Min(4096, size)];
            await stream.ReadExactlyAsync(sample, ct);
            stream.Position = 0;
            var upload = await uploads.ReserveAsync(file.FileName, ChatAttachmentContent.Identify(file.FileName, sample),
                size, ct, allowEmpty: true);
            context.Artifacts.TrackReservation(this, upload.Id);
            for (int index = 0; index < ChatUploadLimits.ChunkCount(size); index++)
            {
                int length = Math.Min(ChatUploadLimits.ChunkSize, size - index * ChatUploadLimits.ChunkSize);
                if (await uploads.WriteChunkAsync(upload.Id, index, stream, length, ct) != ChunkWriteResult.Saved)
                    throw new IOException("Não foi possível guardar o arquivo gerado.");
            }
            staged.Add(file.FileName, upload.Id);
        }
        return staged;
    }

    public async Task DiscardPendingAsync(IReadOnlyList<Guid> ids)
    {
        if (ids.Count == 0) return;
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<AegisDbContext>();
            var uploads = ids.ToArray();
            await db.ChatUploads.Where(u => uploads.Contains(u.Id) && u.AttachmentId == null).ExecuteDeleteAsync();
        }
        catch (Exception e)
        {
            // The ordinary upload expiry worker also recovers after a host restart.
            logger.LogWarning("Generated upload cleanup deferred: {ErrorType}", e.GetType().Name);
        }
    }
}
