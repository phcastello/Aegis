using System.Text.Json;
using Aegis.Application.Common;
using Aegis.Domain.Entities;

namespace Aegis.Application.Tools;

internal sealed record ScriptRecipeReference(Guid ConversationId, Guid ExecutionId);

public static class ScriptRecipeReferences
{
    public const string Scope = "script_workspace";
    public const string Execution = "script_execution";
    public const string Delivery = "script_delivery";
    public const string Output = "script_output";

    internal static void SaveDelivery(IAegisDbContext db, Guid messageId, ScriptRecipeReference? latest,
        IEnumerable<(ChatAttachment Attachment, ScriptRecipeReference Recipe)> outputs)
    {
        if (latest is not null)
            Add(db, latest, Delivery, messageId);
        foreach (var (attachment, recipe) in outputs)
            Add(db, recipe, Output, attachment.Id);
    }

    private static void Add(IAegisDbContext db, ScriptRecipeReference recipe, string type, Guid key) =>
        db.AddToolContextEntry(new ToolContextEntry(recipe.ConversationId, Scope, type, key.ToString("N"),
            JsonSerializer.Serialize(new { recipe.ExecutionId }), PythonScriptTool.ToolName, DateTimeOffset.MaxValue));
}
