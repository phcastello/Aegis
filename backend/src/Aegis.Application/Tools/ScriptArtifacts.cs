using Aegis.Application.Chat;
using Aegis.Application.Common;

namespace Aegis.Application.Tools;

public sealed record ScriptArtifactFile(string FileName, string FullPath);

public interface IScriptArtifactStore
{
    Task<IReadOnlyDictionary<string, Guid>> StageAsync(IReadOnlyList<ScriptArtifactFile> files, ToolExecutionContext context, CancellationToken ct);
    Task DiscardPendingAsync(IReadOnlyList<Guid> ids);
}

// Each turn owns its staged uploads until its assistant message commits.
public sealed class ScriptArtifacts : IAsyncDisposable
{
    private readonly Dictionary<string, Guid> files = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<Guid, ScriptRecipeReference> recipes = [];
    private readonly List<Guid> reservations = [];
    private IScriptArtifactStore? store;
    private ScriptRecipeReference? latestRecipe;

    public void CheckFileCount(IEnumerable<string> names)
    {
        if (files.Keys.Union(names, files.Comparer).Count() > ChatUploadLimits.MaxFiles)
            throw new ArgumentException($"O resultado pode conter até {ChatUploadLimits.MaxFiles} arquivos.");
    }

    public void TrackReservation(IScriptArtifactStore owner, Guid uploadId)
    {
        store = owner;
        reservations.Add(uploadId);
    }

    public void Publish(IReadOnlyDictionary<string, Guid> uploads)
    {
        foreach (var (name, id) in uploads) files[name] = id;
    }

    public void RecordRecipe(Guid conversationId, Guid executionId, IEnumerable<Guid> outputUploads)
    {
        var recipe = new ScriptRecipeReference(conversationId, executionId);
        latestRecipe = recipe;
        foreach (var id in outputUploads)
            recipes[id] = recipe;
    }

    public async Task<IReadOnlyList<ChatAttachmentResponse>?> AttachAsync(IAegisDbContext db, Guid messageId, CancellationToken ct)
    {
        if (files.Count == 0)
        {
            ScriptRecipeReferences.SaveDelivery(db, messageId, latestRecipe, []);
            return null;
        }
        var attachments = await db.ResolveChatUploadsAsync(messageId, files.Values.ToArray(), ct);
        db.AddChatAttachments(attachments);
        ScriptRecipeReferences.SaveDelivery(db, messageId, latestRecipe,
            attachments.Where(a => a.Upload is not null && recipes.ContainsKey(a.Upload.Id))
                .Select(a => (a, recipes[a.Upload!.Id])));
        return attachments.Select(a => new ChatAttachmentResponse(a.Id, a.FileName, a.ContentType, a.SizeBytes)).ToArray();
    }

    public async ValueTask DisposeAsync()
    {
        if (store is not null)
            await store.DiscardPendingAsync(reservations);
    }
}
