using Aegis.Application.Models;
using System.Text.Json;

namespace Aegis.Application.Chat;

internal sealed record ResearchDelegation(string Intent, string Brief)
{
    public const string ToolName = "delegate_web_research";
    private const int MaximumIntentLength = 300;

    public static ModelToolDefinition Definition { get; } = new(ToolName,
        "Delegue uma pesquisa web que provavelmente exigirá tempo, por exemplo várias verificações independentes, " +
        "comparação entre fontes ou levantamento amplo. Use antes de iniciar a pesquisa, como única chamada e sem texto separado. " +
        "Não use para uma consulta rápida, se o usuário pediu para não pesquisar, ou para executar uma ação externa.",
        JsonSerializer.SerializeToElement(new
        {
            type = "object",
            properties = new
            {
                intent = new
                {
                    type = "string", minLength = 1, maxLength = MaximumIntentLength,
                    description = "Uma frase em linguagem natural dizendo ao usuário o que você vai pesquisar. Pode ser falada imediatamente. Não prometa que o resultado já está pronto."
                },
                brief = new
                {
                    type = "string", minLength = 1, maxLength = IResearchDelegationQueue.MaximumBriefLength,
                    description = "Pedido de pesquisa autossuficiente com objetivo, referências públicas e restrições relevantes do diálogo. Preserve o que o usuário quer saber. Exclua mensagens privadas, dados pessoais, credenciais, localização exata e memórias."
                }
            },
            required = new[] { "intent", "brief" },
            additionalProperties = false
        }));

    public static ResearchDelegation? Read(ModelToolRequest round, IReadOnlyList<ModelToolCall> calls)
    {
        var call = calls.FirstOrDefault(call => call.Name == ToolName);
        if (call is null) return null;
        var args = call.Arguments;
        if (calls.Count != 1 || !round.Tools.Any(tool => tool.Name == ToolName) ||
            args.ValueKind != JsonValueKind.Object || args.EnumerateObject().Count() != 2 ||
            !TryReadText(args, "intent", MaximumIntentLength, out var intent) ||
            !TryReadText(args, "brief", IResearchDelegationQueue.MaximumBriefLength, out var brief))
            throw new InvalidOperationException("Delegação de pesquisa inválida ou indisponível.");

        return new(intent, brief);
    }

    private static bool TryReadText(JsonElement args, string name, int maximum, out string text)
    {
        text = args.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()!.Trim()
            : "";
        return text.Length > 0 && text.Length <= maximum &&
            !text.Any(character => char.IsControl(character) && character is not ('\n' or '\r' or '\t'));
    }
}
