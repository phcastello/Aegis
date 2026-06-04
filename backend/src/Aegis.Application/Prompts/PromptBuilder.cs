using System.Text;
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
        CancellationToken cancellationToken = default)
    {
        var identity = await IdentityPrompt.Value.WaitAsync(cancellationToken);
        var runtimeContext = await runtimeContextProvider.GetRuntimeContextAsync(cancellationToken);

        var prompt = new StringBuilder();
        prompt.AppendLine(identity.Trim());
        prompt.AppendLine();

        if (!string.IsNullOrWhiteSpace(runtimeContext))
        {
            prompt.AppendLine(runtimeContext.Trim());
            prompt.AppendLine();
        }

        prompt.AppendLine("Recent conversation history:");
        if (recentHistory.Count == 0)
        {
            prompt.AppendLine("No prior messages in this conversation.");
        }
        else
        {
            foreach (var message in recentHistory.OrderBy(message => message.CreatedAt).ThenBy(message => message.Id))
            {
                prompt.AppendLine($"{GetPromptRoleLabel(message.Role)}: {message.Content}");
            }
        }

        prompt.AppendLine();
        prompt.AppendLine("Current user message:");
        prompt.AppendLine($"User: {currentUserMessage.Trim()}");
        prompt.AppendLine();
        prompt.AppendLine("Respond as Aegis.");

        return new PromptBuildResult(prompt.ToString().Trim(), runtimeContext);
    }

    private static async Task<string> LoadIdentityPromptAsync()
    {
        var path = Path.Combine(AppContext.BaseDirectory, IdentityPromptRelativePath);
        if (File.Exists(path))
        {
            return await File.ReadAllTextAsync(path);
        }

        return """
            # Aegis Identity

            You are Aegis.

            Your current version codename is "Hello, Aegis".
            """;
    }

    private static string GetPromptRoleLabel(string role)
    {
        return role switch
        {
            ChatRoles.User => "User",
            ChatRoles.Assistant => "Aegis",
            ChatRoles.System => "System",
            _ => role
        };
    }
}
