using System.Text.Json;
using Aegis.Application.Tools;
using Aegis.Domain.Entities;
namespace Aegis.Application.Reminders;

public abstract class ReminderSeriesToolBase : IAegisTool
{
    protected static JsonElement Schema(string value) => JsonSerializer.Deserialize<JsonElement>(value);
    protected static AegisToolResult Ok(object data) => new(true, JsonSerializer.Serialize(data, ReminderRecurrenceRule.JsonOptions));
    public abstract string Name { get; }
    public abstract string Description { get; }
    public abstract JsonElement ParametersSchema { get; }
    public async Task<AegisToolResult> ExecuteAsync(JsonElement arguments, ToolExecutionContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            if (arguments.ValueKind != JsonValueKind.Object || arguments.EnumerateObject().Any(p => !ParametersSchema.GetProperty("properties").TryGetProperty(p.Name, out _))) throw new ArgumentException("Campos desconhecidos.");
            if (ParametersSchema.TryGetProperty("required", out var required) && required.EnumerateArray().Any(p => !arguments.TryGetProperty(p.GetString()!, out var value) || value.ValueKind == JsonValueKind.Null)) throw new ArgumentException("Campos obrigatórios ausentes.");
            return await Run(arguments, context, cancellationToken);
        }
        catch (ReminderException e) { return new(false, JsonSerializer.Serialize(new { error = e.Code, message = e.Message }), e.Code); }
        catch (Exception e) when (e is ArgumentException or JsonException or InvalidOperationException or TimeZoneNotFoundException or InvalidTimeZoneException or FormatException)
        { return new(false, JsonSerializer.Serialize(new { error = "invalid_tool_arguments", message = "Regra ou referência inválida. Consulte a lista, use IDs e revisão observados e esclareça alvos ambíguos." }), "invalid_tool_arguments"); }
    }
    protected abstract Task<AegisToolResult> Run(JsonElement arguments, ToolExecutionContext context, CancellationToken ct);
    protected const string RuleSchema = """{"type":"object","properties":{"frequency":{"type":"string","enum":["daily","weekly","monthly"]},"interval":{"type":"integer","minimum":1,"maximum":365},"localTime":{"type":"string","description":"Hora local HH:mm no timezone da série."},"startDate":{"type":"string","description":"Data-base YYYY-MM-DD; intervalos semanais começam segunda-feira."},"weekdays":{"type":"array","items":{"type":"integer","minimum":1,"maximum":7}},"monthDay":{"type":"integer","minimum":1,"maximum":31,"description":"Dia inexistente pula aquele mês; último dia é regra distinta."},"lastDay":{"type":"boolean"},"untilDate":{"type":"string"},"count":{"type":"integer","minimum":1,"maximum":10000}},"required":["frequency","interval","localTime","startDate"],"additionalProperties":false}""";
}
public sealed class ReminderSeriesCreateTool(ReminderSeriesService service) : ReminderSeriesToolBase
{
    public override string Name => "reminder_series_create";
    public override string Description => "Cria uma série de lembretes após pedido explícito, como todo dia às oito, a cada três dias, semanal nos dias selecionados, mensal no dia 5 ou último dia do mês. Envie regra estruturada, nunca cron. Use timezone IANA, data-base e hora local. Se horário/alvo realmente ambíguo, esclareça; não peça confirmação extra para pedido suficiente. count ou untilDate encerram; omitir ambos = sem fim. Dia 31 pula meses sem31 e deve ser explicado. Não cria automação geográfica. Lembretes pessoais vão a todos dispositivos habilitados por padrão, sem áudio em broadcast.";
    public override JsonElement ParametersSchema { get; } = Schema("""{"type":"object","properties":{"text":{"type":"string","minLength":1,"maxLength":600},"timeZoneId":{"type":"string"},"deliveryPolicy":{"type":"string","enum":["allEligible","bestAvailable"]},"rule":RULE},"required":["text","timeZoneId","rule"],"additionalProperties":false}""".Replace("RULE", RuleSchema));
    private sealed record Args(string Text, string TimeZoneId, ReminderRecurrenceRule Rule, string DeliveryPolicy = "allEligible");
    protected override async Task<AegisToolResult> Run(JsonElement arguments, ToolExecutionContext context, CancellationToken ct)
    { var a = arguments.Deserialize<Args>(ReminderRecurrenceRule.JsonOptions)!; return Ok(ReminderSeriesService.View(await service.CreateAsync(context.ConversationId, a.Text, a.TimeZoneId, a.Rule, a.DeliveryPolicy, ct))); }
}
public sealed class ReminderSeriesListTool(ReminderSeriesService service) : ReminderSeriesToolBase
{
    public override string Name => "reminder_series_list";
    public override string Description => "Lista uma página de até100 séries recorrentes reais e registra somente seus IDs/revisões observados por30min. Se hasMore=true, consulte novamente com offset=nextOffset antes de afirmar que não há outra série/alvo; primeira página usa offset0. Use antes de mudar/pausar/retomar/pular/cancelar uma série não observada. Lembretes únicos e ocorrências são reminder_list. Não invente ID; esclareça múltiplos alvos.";
    public override JsonElement ParametersSchema { get; } = Schema("""{"type":"object","properties":{"offset":{"type":"integer","minimum":0,"maximum":1000000}},"additionalProperties":false}""");
    private sealed record Args(int Offset = 0);
    protected override async Task<AegisToolResult> Run(JsonElement arguments, ToolExecutionContext context, CancellationToken ct)
    { var args = arguments.Deserialize<Args>(ReminderRecurrenceRule.JsonOptions)!; return Ok(ReminderSeriesService.PageView(await service.PageAsync(context.ConversationId, args.Offset, ct))); }
}
public sealed class ReminderSeriesChangeTool(ReminderSeriesService service) : ReminderSeriesToolBase
{
    public override string Name => "reminder_series_change";
    public override string Description => "Altera uma série observada: operation update,pause,resume,skip,cancel. 'Pausa esse lembrete diário', 'pula o próximo', 'muda a série para nove' afetam série; 'adia só hoje' usa reminder_action. Use seriesId e expectedRevision reais observados e esclareça ambiguidade. Alteração afeta futuras, preserva ocorrências disparadas; retomada ignora atrasados da pausa. Ao editar rule envie regra completa observada com campos desejados alterados, sem cron.";
    public override JsonElement ParametersSchema { get; } = Schema("""{"type":"object","properties":{"seriesId":{"type":"string"},"expectedRevision":{"type":"integer","minimum":1},"operation":{"type":"string","enum":["update","pause","resume","skip","cancel"]},"text":{"type":"string"},"rule":RULE,"deliveryPolicy":{"type":"string","enum":["allEligible","bestAvailable"]}},"required":["seriesId","expectedRevision","operation"],"additionalProperties":false}""".Replace("RULE", RuleSchema));
    private sealed record Args(Guid SeriesId, int ExpectedRevision, string Operation, string? Text = null, ReminderRecurrenceRule? Rule = null, string? DeliveryPolicy = null);
    protected override async Task<AegisToolResult> Run(JsonElement arguments, ToolExecutionContext context, CancellationToken ct)
    { var a = arguments.Deserialize<Args>(ReminderRecurrenceRule.JsonOptions)!; return Ok(ReminderSeriesService.View(await service.ChangeAsync(context.ConversationId, a.SeriesId, a.ExpectedRevision, a.Operation, a.Text, a.Rule, a.DeliveryPolicy, ct))); }
}
public sealed class ReminderActionTool(ReminderService reminders, ReminderActionService actions) : ReminderSeriesToolBase
{
    public override string Name => "reminder_action";
    public override string Description => "Reconhece (OK) ou adia uma ocorrência já disparada: OK significa reconheci, não tarefa concluída. Adiar só hoje usa snooze de10min por padrão, sem mover próximas da série. Use reminderId, expectedRevision e generation reais observados em reminder_list. actionId UUID novo identifica o gesto e deve ser reutilizado no retry. A primeira ação válida vence globalmente; revisão antiga retorna estado atual. Abrir não é OK.";
    public override JsonElement ParametersSchema { get; } = Schema("""{"type":"object","properties":{"reminderId":{"type":"string"},"actionId":{"type":"string"},"expectedRevision":{"type":"integer","minimum":1},"generation":{"type":"integer","minimum":1},"action":{"type":"string","enum":["acknowledge","snooze"]},"snoozeMinutes":{"type":"integer","minimum":1,"maximum":1440}},"required":["reminderId","actionId","expectedRevision","generation","action"],"additionalProperties":false}""");
    private sealed record Args(Guid ReminderId, Guid ActionId, int ExpectedRevision, int Generation, string Action, int SnoozeMinutes = 10);
    protected override async Task<AegisToolResult> Run(JsonElement arguments, ToolExecutionContext context, CancellationToken ct)
    {
        var a = arguments.Deserialize<Args>(ReminderRecurrenceRule.JsonOptions)!; await reminders.EnsureObservedAsync(context.ConversationId, a.ReminderId, ct);
        return Ok(await actions.ApplyAsync(a.ReminderId, new(a.ActionId, a.ExpectedRevision, a.Generation, a.Action, a.SnoozeMinutes), ct));
    }
}
