namespace Aegis.Application.Models;

public sealed class AegisModelRouter
{
    private static readonly string[] StrongerModelTerms =
    [
        "modelo principal",
        "modelo forte",
        "modelo parrudo",
        "modelo de ponta",
        "usa o mini",
        "use o mini",
        "escala isso",
        "escalona isso"
    ];

    private static readonly string[] OperationalTerms =
    [
        "email",
        "e-mail",
        "gmail",
        "inbox",
        "caixa de entrada",
        "agenda",
        "calendário",
        "calendario",
        "tarefa",
        "afazer",
        "lembrete",
        "lembrar",
        "whatsapp",
        "mensagem",
        "reunião",
        "reuniao",
        "compromisso",
        "marcar",
        "enviar",
        "responder",
        "resumir meus emails",
        "briefing",
        "não lido",
        "nao lido",
        "lido",
        "estrela",
        "importante",
        "professor",
        "unicentro",
        "github",
        "faculdade",
        "boleto",
        "prazo",
        "convite"
    ];

    private static readonly string[] ContextualToolTerms =
    [
        "fiz a conexão",
        "fiz a conexao",
        "conectei",
        "faz o que achar melhor",
        "cadê",
        "cade",
        "tente novamente",
        "tentar novamente",
        "confirma",
        "confirmo",
        "sim",
        "agora foi",
        "deu certo",
        "confere",
        "confira",
        "verifica",
        "verifique",
        "valida",
        "valide",
        "releia",
        "já confirmo",
        "ja confirmo"
    ];

    public ModelPurpose ChoosePurpose(ChatRequestContext context)
    {
        if (context.RequiresTools || context.HasPendingAction ||
            (context.HasRecentToolContext && ContainsAny(context.UserContent, ContextualToolTerms)))
        {
            return ModelPurpose.Main;
        }

        if (ContainsAny(context.UserContent, StrongerModelTerms) ||
            ContainsAny(context.UserContent, OperationalTerms))
        {
            return ModelPurpose.Main;
        }

        return ModelPurpose.Default;
    }

    public bool RequiresTools(ChatRequestContext context)
    {
        return context.RequiresTools ||
            context.HasPendingAction ||
            (context.HasRecentToolContext && ContainsAny(context.UserContent, ContextualToolTerms)) ||
            ContainsAny(context.UserContent, OperationalTerms);
    }

    private static bool ContainsAny(string value, IReadOnlyList<string> terms)
    {
        return terms.Any(term => value.Contains(term, StringComparison.OrdinalIgnoreCase));
    }
}
