using System.Text.Json;
using Aegis.Application.Runtime;
using Aegis.Domain;
using Aegis.Domain.Entities;

namespace Aegis.Application.Prompts;

public sealed class PromptBuilder(IRuntimeContextProvider runtimeContextProvider) : IPromptBuilder
{
    private const string IdentityPromptRelativePath = "Prompts/aegis_identity.md";
    private static readonly Lazy<Task<string>> IdentityPrompt = new(LoadIdentityPromptAsync);

    public async Task<PromptBuildResult> BuildPromptAsync(
        IReadOnlyList<ChatMessage> recentHistory,
        string currentUserMessage,
        string? pendingEmailActionState = null,
        CancellationToken cancellationToken = default)
    {
        var identity = (await IdentityPrompt.Value.WaitAsync(cancellationToken)).Trim();
        var runtimeContext = await runtimeContextProvider.GetRuntimeContextAsync(cancellationToken);
        var dynamicContext = string.Join("\n", new[] { runtimeContext?.Trim(), pendingEmailActionState?.Trim() }
            .Where(part => !string.IsNullOrWhiteSpace(part)));
        var input = new List<JsonElement>
        {
            JsonSerializer.SerializeToElement(new
            {
                role = "developer",
                content = new[] { new
                {
                    type = "input_text",
                    text = identity,
                    prompt_cache_breakpoint = new { mode = "explicit" }
                } }
            })
        };

        foreach (var message in recentHistory.OrderBy(message => message.CreatedAt).ThenBy(message => message.Id))
        {
            var role = message.Role switch
            {
                ChatRoles.User => "user",
                ChatRoles.Assistant => "assistant",
                _ => null
            };
            if (role is not null)
            {
                input.Add(JsonSerializer.SerializeToElement(new { role, content = message.Content }));
            }
        }

        input.Add(JsonSerializer.SerializeToElement(new { role = "user", content = currentUserMessage }));
        // The current user message becomes a reusable implicit cache boundary on the next turn.
        // Runtime values must follow it so they do not interrupt the growing conversation prefix.
        if (!string.IsNullOrWhiteSpace(dynamicContext))
        {
            input.Add(JsonSerializer.SerializeToElement(new
            {
                role = "developer",
                content = "Contexto operacional (use apenas quando relevante):\n" + dynamicContext
            }));
        }
        return new PromptBuildResult(identity, dynamicContext, input);
    }

    private static async Task<string> LoadIdentityPromptAsync()
    {
        var path = Path.Combine(AppContext.BaseDirectory, IdentityPromptRelativePath);
        return File.Exists(path)
            ? await File.ReadAllTextAsync(path)
            : "Você é a Aegis, assistente pessoal e residencial. Responda em português brasileiro.";
    }
}
