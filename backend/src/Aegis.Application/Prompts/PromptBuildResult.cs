using System.Text.Json;
using System.Text.RegularExpressions;

namespace Aegis.Application.Prompts;

public sealed partial record PromptBuildResult(
    string Prompt,
    string? RuntimeContext,
    IReadOnlyList<JsonElement> InputItems,
    string? AuditRuntimeContext = null,
    IReadOnlyList<JsonElement>? AuditInputItems = null)
{
    [GeneratedRegex(@"^### Jeito de falar[ \t]*\r?\n(.*?)(?=^#{1,3}(?:[ \t]|\r?$)|\z)", RegexOptions.Multiline | RegexOptions.Singleline)]
    private static partial Regex ReplyStyleSection();

    public string? ReplyStyleInstructions
    {
        get
        {
            var body = ReplyStyleSection().Match(Prompt).Groups[1].Value.Trim();
            return body.Length > 0 ? body : null;
        }
    }

    public IReadOnlyList<Guid> UsedMemoryIds { get; init; } = [];
    public bool ContainsAttachments { get; init; }
    [System.Text.Json.Serialization.JsonIgnore]
    public Aegis.Application.Chat.ChatAttachmentContent? AttachmentReader { get; init; }
    public IReadOnlyList<Guid> UsedObservedMemoryIds { get; init; } = [];
    public IReadOnlyList<Guid> UsedRelationIds { get; init; } = [];
}
