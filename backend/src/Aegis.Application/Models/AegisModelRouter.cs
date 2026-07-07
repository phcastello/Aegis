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
        "briefing"
    ];

    public ModelPurpose ChoosePurpose(ChatRequestContext context)
    {
        if (context.RequiresTools || context.HasPendingAction)
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

    private static bool ContainsAny(string value, IReadOnlyList<string> terms)
    {
        return terms.Any(term => value.Contains(term, StringComparison.OrdinalIgnoreCase));
    }
}
