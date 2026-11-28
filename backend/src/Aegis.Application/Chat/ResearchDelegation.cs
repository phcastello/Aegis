using Aegis.Application.Models;
using System.Text.Json;

namespace Aegis.Application.Chat;

internal sealed record ResearchDelegation(string Intent, string Brief, bool DelegatePython, bool DelegateVideo)
{
    public const string ToolName = "delegate_web_research";
    public const string PythonToolName = "delegate_python";
    public const string VideoToolName = "delegate_video_analysis";
    private const int MaximumIntentLength = 300;

    public static ModelToolDefinition Definition { get; } = CreateDefinition(ToolName,
        "Delegue uma pesquisa web que provavelmente exigirá tempo, por exemplo várias verificações independentes, " +
        "comparação entre fontes ou levantamento amplo. Use antes de iniciar a pesquisa, como única chamada e sem texto separado. " +
        "Não use para uma consulta rápida, se o usuário pediu para não pesquisar, ou para executar uma ação externa.",
        "Uma frase em linguagem natural dizendo ao usuário o que você vai pesquisar. Pode ser falada imediatamente. Não prometa que o resultado já está pronto.",
        "Pedido de pesquisa autossuficiente com objetivo, referências públicas e restrições relevantes do diálogo. Preserve o que o usuário quer saber. Exclua mensagens privadas, dados pessoais, credenciais, localização exata e memórias.");

    public static ModelToolDefinition PythonDefinition { get; } = CreateDefinition(PythonToolName,
        "Delegue um trabalho em Python que você prevê que levará várias etapas ou uma espera perceptível. " +
        "Use antes da primeira execução, como única chamada e sem texto separado. " +
        "O agente delegado só tem Python isolado, sem rede. Para sorteios e cálculos breves, use run_python no turno atual.",
        "Uma frase breve dizendo o que você vai calcular ou analisar. Ela pode ser falada imediatamente.",
        "Instrução autossuficiente com o objetivo, os dados necessários, as restrições e o resultado desejado. " +
        "Preserve os dados exatos do pedido. Inclua apenas o que o usuário indicou para esta tarefa. Exclua credenciais e memórias.");

    public static ModelToolDefinition VideoDefinition { get; } = CreateDefinition(VideoToolName,
        "Delegue uma análise de vídeo anexado que deve levar tempo, como transcrever a fala ou buscar um momento desconhecido. " +
        "Use antes da primeira inspeção, como única chamada. Para olhar um trecho curto do vídeo, use inspect_video no próprio turno.",
        "Uma frase breve que diga qual trecho, ação ou fala você vai verificar.",
        "Pedido autossuficiente com o vídeo e o que deve ser observado. Preserve intervalos, nomes de arquivo e objetivo.");

    private static ModelToolDefinition CreateDefinition(string name, string description, string intentDescription,
        string briefDescription) => new(name, description, JsonSerializer.SerializeToElement(new
        {
            type = "object",
            properties = new
            {
                intent = new
                {
                    type = "string", minLength = 1, maxLength = MaximumIntentLength,
                    description = intentDescription
                },
                brief = new
                {
                    type = "string", minLength = 1, maxLength = IResearchDelegationQueue.MaximumBriefLength,
                    description = briefDescription
                }
            },
            required = new[] { "intent", "brief" },
            additionalProperties = false
        }));

    public static ResearchDelegation? Read(ModelToolRequest round, IReadOnlyList<ModelToolCall> calls)
    {
        var call = calls.FirstOrDefault(call => call.Name is ToolName or PythonToolName or VideoToolName);
        if (call is null) return null;
        var args = call.Arguments;
        if (calls.Count != 1 || !round.Tools.Any(tool => tool.Name == call.Name) ||
            args.ValueKind != JsonValueKind.Object || args.EnumerateObject().Count() != 2 ||
            !TryReadText(args, "intent", MaximumIntentLength, out var intent) ||
            !TryReadText(args, "brief", IResearchDelegationQueue.MaximumBriefLength, out var brief))
            throw new InvalidOperationException("Delegação inválida ou indisponível.");

        return new(intent, brief, call.Name == PythonToolName, call.Name == VideoToolName);
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
