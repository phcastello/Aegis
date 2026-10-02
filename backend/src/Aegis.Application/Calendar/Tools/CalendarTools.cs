using System.Text.Json;
using System.Text.Json.Serialization;
using Aegis.Application.Common;
using Aegis.Application.Email;
using Aegis.Application.Tools;
using Aegis.Domain;
using Aegis.Domain.Entities;
using Microsoft.Extensions.Logging;

namespace Aegis.Application.Calendar.Tools;

public abstract class CalendarToolBase : IAegisTool
{
    protected static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };
    public abstract string Name { get; }
    public abstract string Description { get; }
    public abstract JsonElement ParametersSchema { get; }
    public async Task<AegisToolResult> ExecuteAsync(JsonElement arguments, ToolExecutionContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            if (arguments.ValueKind != JsonValueKind.Object) return Error("invalid_calendar_tool_arguments", "Parâmetros devem ser um objeto.");
            var properties = ParametersSchema.GetProperty("properties");
            if (arguments.EnumerateObject().Any(p => !properties.TryGetProperty(p.Name, out _)))
                return Error("invalid_calendar_tool_arguments", "Campo desconhecido. Use os parâmetros do schema.");
            if (ParametersSchema.TryGetProperty("required", out var required) && required.EnumerateArray().Any(p =>
                !arguments.TryGetProperty(p.GetString()!, out var value) || value.ValueKind == JsonValueKind.Null))
                return Error("invalid_calendar_tool_arguments", "Faltam parâmetros obrigatórios. Obtenha os dados antes de preparar a ação.");
            if (arguments.TryGetProperty("description", out var description) && description.ValueKind != JsonValueKind.String)
                return Error("invalid_calendar_tool_arguments", "description deve ser texto: omita para preservar a anotação, envie texto para substituir ou string vazia para limpar. Não use null.");
            if (arguments.TryGetProperty("recurrence", out var recurrence) && recurrence.ValueKind != JsonValueKind.Object)
                return Error("invalid_calendar_tool_arguments", "recurrence deve ser um objeto; omita para preservar na emenda ou use frequency=none para remover.");
            return await RunAsync(arguments, context, cancellationToken);
        }
        catch (CalendarException e) { return Error(e.Code, e.Message); }
        catch (EmailConnectionException) { return Error("calendar_authorization_failed", "Não consegui gerar a autorização Google. Verifique a configuração da conexão."); }
        catch (JsonException) { return Error("invalid_calendar_tool_arguments", "Parâmetros inválidos. Confira os tipos e campos do schema."); }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { return Error("calendar_temporarily_unavailable", "Google demorou a responder. Tente novamente."); }
        catch (HttpRequestException) { return Error("calendar_temporarily_unavailable", "Google está temporariamente indisponível. Tente novamente."); }
    }
    protected abstract Task<AegisToolResult> RunAsync(JsonElement arguments, ToolExecutionContext context, CancellationToken cancellationToken);
    protected static JsonElement Schema(string value) => JsonSerializer.Deserialize<JsonElement>(value);
    protected static AegisToolResult Ok(object data) => new(true, JsonSerializer.Serialize(data, JsonOptions));
    protected static AegisToolResult Error(string code, string message) => new(false,
        JsonSerializer.Serialize(new { error = code, message }, JsonOptions), code == "invalid_calendar_tool_arguments" ? "invalid_tool_arguments" : code);
    protected static string RequiredId(JsonElement args) => args.TryGetProperty("eventId", out var id) && id.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(id.GetString())
        ? id.GetString()! : throw new CalendarException("invalid_calendar_tool_arguments", "eventId observado anteriormente é obrigatório. Consulte calendar_list_events.");
    protected static string? OptionalCalendarId(JsonElement args)
    {
        if (!args.TryGetProperty("calendarId", out var id) || id.ValueKind == JsonValueKind.Null) return null;
        if (id.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(id.GetString()) || id.GetString()!.Length > 1024)
            throw new CalendarException("invalid_calendar_tool_arguments", "calendarId deve ser um ID observado, não o nome do calendário.");
        return id.GetString();
    }
    protected static JsonElement EventReferenceSchema { get; } = Schema("""{"type":"object","properties":{"eventId":{"type":"string"},"calendarId":{"type":["string","null"],"description":"Calendário de origem observado; pode omitir se eventId identifica um único evento recente."}},"required":["eventId"],"additionalProperties":false}""");
    protected static void RequireEmpty(JsonElement args)
    {
        if (args.EnumerateObject().Any()) throw new CalendarException("invalid_calendar_tool_arguments", "Esta ferramenta não recebe parâmetros.");
    }
}

public sealed class CalendarGetStatusTool(ICalendarService calendar) : CalendarToolBase
{
    public override string Name => "calendar_get_status";
    public override string Description => "Consulta a conexão Google compartilhada com Gmail, a conta e se Calendar Events e Calendar List estão autorizados. Gmail pode continuar conectado quando faltar um scope Calendar.";
    public override JsonElement ParametersSchema { get; } = Schema("""{"type":"object","properties":{},"additionalProperties":false}""");
    protected override async Task<AegisToolResult> RunAsync(JsonElement arguments, ToolExecutionContext context, CancellationToken cancellationToken)
    { RequireEmpty(arguments); return Ok(new { status = await calendar.GetStatusAsync(cancellationToken) }); }
}

public sealed class CalendarCreateConnectLinkTool(IEmailConnectionService connection) : CalendarToolBase
{
    public override string Name => "calendar_create_connect_link";
    public override string Description => "Gera autorização Google combinada Gmail + Calendar Events + Calendar List readonly na mesma conexão. Use para conectar ou acrescentar scopes Calendar à mesma conta após pedido direto bloqueado. Gere na mesma interação sem confirmação de mutação; não cria o evento. Não use para falha temporária.";
    public override JsonElement ParametersSchema { get; } = Schema("""{"type":"object","properties":{},"additionalProperties":false}""");
    protected override async Task<AegisToolResult> RunAsync(JsonElement arguments, ToolExecutionContext context, CancellationToken cancellationToken)
    { RequireEmpty(arguments); return Ok(new { authorizationUrl = (await connection.CreateConnectLinkAsync(cancellationToken)).AuthorizationUrl }); }
}

public sealed class CalendarListCalendarsTool(ICalendarService calendar, CalendarToolContextService toolContext) : CalendarToolBase
{
    public override string Name => "calendar_list_calendars";
    public override string Description => "Lista as agendas acessíveis da conta Google, com calendarId, nome, primary, nível de acesso, calendarType e defaultReminders reais quando disponíveis. Use para consultar os lembretes padrão de uma agenda ou resolver um destino solicitado pelo usuário. Feriados são contexto do dia, não compromissos. Se nomes forem ambíguos, esclareça antes de preparar criação. Escrita exige acesso writer/owner.";
    public override JsonElement ParametersSchema { get; } = Schema("""{"type":"object","properties":{},"additionalProperties":false}""");
    protected override async Task<AegisToolResult> RunAsync(JsonElement arguments, ToolExecutionContext context, CancellationToken cancellationToken)
    {
        RequireEmpty(arguments);
        var result = await calendar.ListCalendarsAsync(cancellationToken);
        await toolContext.RememberCalendarsAsync(context.ConversationId, result.Calendars, Name, cancellationToken);
        return Ok(result);
    }
}

public sealed class CalendarListEventsTool(ICalendarService calendar, CalendarToolContextService toolContext) : CalendarToolBase
{
    public override string Name => "calendar_list_events";
    public override string Description => "Lista ou pesquisa eventos de todas as agendas acessíveis com leitura de eventos, separados em events (compromissos) e holidays (feriados/contexto do dia), ordenados cronologicamente em cada lista. O limite global prioriza compromissos; hasMoreEvents/hasMoreHolidays indicam resultados omitidos. Feriados não significam ocupação ou conflito de horário. Cada item inclui calendarId, calendarName e eventId de origem. Para consultar anotações, use calendar_get_event: a listagem compacta omite description. Use timeMin/timeMax com offset e o timezone do contexto operacional para intervalos; query para busca textual. Para o próximo compromisso, use timeMin atual e limit 1. IDs retornados podem ser usados nesta conversa.";
    public override JsonElement ParametersSchema { get; } = Schema("""
        {"type":"object","properties":{
          "timeMin":{"type":["string","null"],"description":"Limite inferior RFC3339 com offset, ex. 2026-10-10T00:00:00-03:00."},
          "timeMax":{"type":["string","null"],"description":"Limite superior exclusivo RFC3339 com offset."},
          "query":{"type":["string","null"],"description":"Texto para pesquisar título, descrição e local."},
          "limit":{"type":"integer","minimum":1,"maximum":50,"description":"Máximo GLOBAL de events + holidays, padrão 20; compromissos têm prioridade quando o limite é atingido."}
        },"additionalProperties":false}
        """);
    private sealed record Arguments(string? TimeMin = null, string? TimeMax = null, string? Query = null, int Limit = 20);
    protected override async Task<AegisToolResult> RunAsync(JsonElement arguments, ToolExecutionContext context, CancellationToken cancellationToken)
    {
        var args = arguments.Deserialize<Arguments>(JsonOptions)!;
        var result = await calendar.ListEventsAsync(args.TimeMin, args.TimeMax, args.Query, args.Limit, cancellationToken);
        await toolContext.RememberAsync(context.ConversationId, result.Events.Concat(result.Holidays), Name, cancellationToken);
        return Ok(result);
    }
}

public sealed class CalendarGetEventTool(ICalendarService calendar, CalendarToolContextService toolContext) : CalendarToolBase
{
    public override string Name => "calendar_get_event";
    public override string Description => "Lê dados atuais, description (anotação nativa) e reminders de um evento no calendário de origem pelo par calendarId + eventId observado em calendar_list_events ou outra tool Calendar nesta conversa. reminders.useDefault=true usa defaults da agenda (consulte calendar_list_calendars); false mostra overrides, e lista vazia significa sem lembretes. Antes de acrescentar conteúdo à anotação, leia description e preserve o texto existente. descriptionTruncated=true indica conteúdo incompleto; não use esse trecho para substituir a anotação completa. Se o contexto expirou, pesquise novamente. type=holiday indica feriado/contexto do dia, sem ocupar horário. Datas de dia inteiro têm fim inclusivo.";
    public override JsonElement ParametersSchema { get; } = EventReferenceSchema;
    protected override async Task<AegisToolResult> RunAsync(JsonElement arguments, ToolExecutionContext context, CancellationToken cancellationToken)
    {
        var id = RequiredId(arguments);
        var observed = await toolContext.ResolveEventAsync(context.ConversationId, id, OptionalCalendarId(arguments), cancellationToken);
        var item = await calendar.GetEventAsync(observed.CalendarId, id, cancellationToken);
        await toolContext.RememberAsync(context.ConversationId, [item], Name, cancellationToken);
        return Ok(new { calendarEvent = item });
    }
}

public abstract class CalendarPrepareActionTool(IAegisDbContext db, ICalendarService calendar, CalendarToolContextService toolContext) : CalendarToolBase
{
    protected abstract string ActionType { get; }
    protected override async Task<AegisToolResult> RunAsync(JsonElement arguments, ToolExecutionContext context, CancellationToken cancellationToken)
    {
        string id;
        string calendarId;
        CalendarEventChanges changes;
        if (ActionType == CalendarActionTypes.Create)
        {
            id = Guid.NewGuid().ToString("N");
            calendarId = OptionalCalendarId(arguments) ?? "primary";
            await toolContext.RequireCalendarAsync(context.ConversationId, calendarId, cancellationToken);
            var fields = arguments.EnumerateObject().Where(p => p.Name != "calendarId").ToDictionary(p => p.Name, p => p.Value);
            changes = JsonSerializer.SerializeToElement(fields).Deserialize<CalendarEventChanges>(JsonOptions)!;
        }
        else
        {
            id = RequiredId(arguments);
            calendarId = (await toolContext.ResolveEventAsync(context.ConversationId, id, OptionalCalendarId(arguments), cancellationToken)).CalendarId;
            var fields = arguments.EnumerateObject().Where(p => p.Name is not "eventId" and not "calendarId").ToDictionary(p => p.Name, p => p.Value);
            changes = JsonSerializer.SerializeToElement(fields).Deserialize<CalendarEventChanges>(JsonOptions)!;
        }
        return await PrepareAndPersistAsync(db, calendar, ActionType, calendarId, id, changes, context, cancellationToken);
    }

    internal static async Task<AegisToolResult> PrepareAndPersistAsync(IAegisDbContext db, ICalendarService calendar, string actionType,
        string calendarId, string eventId, CalendarEventChanges changes, ToolExecutionContext context, CancellationToken cancellationToken)
    {
        if (!await PendingActionGuard.HasValidUserMessageAsync(db, context, cancellationToken: cancellationToken))
            return Error("invalid_calendar_tool_arguments", "A mensagem atual precisa pertencer a esta conversa.");
        var previous = await db.GetUnresolvedPendingCalendarActionsAsync(context.ConversationId, cancellationToken);
        if (previous.Any(action => action.MayHaveAppliedChanges))
            return Error("calendar_action_outcome_unknown", "Há uma tentativa anterior com possíveis efeitos, mesmo se expirada. Verifique o evento ou cancele explicitamente a tentativa pendente; cancelar não reverte efeitos externos.");
        var payload = await calendar.PrepareAsync(actionType, calendarId, eventId, changes, cancellationToken);
        var action = new PendingCalendarAction(context.ConversationId, actionType, eventId,
            JsonSerializer.Serialize(payload, JsonOptions), payload.HumanSummary, DateTimeOffset.UtcNow.AddMinutes(10), calendarId);
        foreach (var old in previous)
        {
            old.Supersede(action.Id);
            db.AddCalendarActionAudit(new(old.Id, context.ConversationId, "supersede_" + old.ActionType, old.EventId,
                context.UserMessageId, true, "Substituída pela proposta " + action.Id, old.CalendarId));
        }
        db.AddPendingCalendarAction(action);
        await db.SaveChangesAsync(cancellationToken);
        return Ok(new { pendingActionId = action.Id, supersededActionIds = previous.Select(old => old.Id), action.ActionType, action.CalendarId, action.EventId, action.HumanSummary, action.ExpiresAt,
            payload.ReminderMode, reminders = payload.Fields["reminders"], holidayWarnings = payload.HolidayWarnings ?? [], payload.HolidayWarningsHasMore,
            userMessage = "Ação preparada. Apresente o resumo e peça confirmação em uma nova mensagem; ainda não executei a alteração." });
    }
    protected static JsonElement MutationSchema(bool create)
    {
        var schema = System.Text.Json.Nodes.JsonNode.Parse("""
        {"type":"object","properties":{
          "summary":{"type":"string","description":"Título do evento."},
          "start":{"type":"string","description":"Início RFC3339 com offset; para dia inteiro, data YYYY-MM-DD."},
          "end":{"type":"string","description":"Fim concreto RFC3339 com offset. Para dia inteiro, última data INCLUSIVA (um dia: end igual a start). Se o usuário delegou a duração/fim, escolha um valor razoável e apresente-o; sem delegação, obtenha a informação ausente."},
          "allDay":{"type":"boolean","description":"true para evento de dia inteiro solicitado ou escolhido sob delegação explícita do horário."},
          "timeZone":{"type":"string","description":"Somente eventos com horário: timezone IANA, padrão do contexto operacional (America/Sao_Paulo). Para allDay=true, omita este campo: o Google usa datas, sem timezone próprio do evento."},
          "description":{"type":"string","maxLength":8000,"description":"Anotação nativa do evento, somente conteúdo solicitado pelo usuário. Omita na criação sem anotação explícita; não gere texto a partir do título/conversa nem inclua metadata interna, IDs, warnings ou lembretes. Na alteração/emenda: omitido preserva, texto substitui, string vazia limpa; não use null. Para acrescentar, leia description em calendar_get_event e envie o conteúdo completo com a nova informação, sem apagar a anterior. Não reconstrua a partir de uma descrição truncada."},
          "location":{"type":"string","description":"Local opcional; string vazia limpa o campo em alteração."},
          "reminderMode":{"type":"string","enum":["keep","aegis_default","calendar_default","custom","none"],"description":"Omitido: criação usa aegis_default; update mantém (keep). aegis_default restaura os lembretes normais da Aegis, com a policy configurada para timed ou all-day; calendar_default usa o padrão real da agenda Google; custom substitui pela lista reminders completa; none remove todos. Use outro modo em update apenas se o usuário pediu mudança de lembretes. keep não é válido na criação de evento novo."},
          "reminders":{"type":"array","maxItems":5,"description":"Somente com reminderMode=custom: lista completa, substitui os overrides. Para adicionar um lembrete, leia e preserve os que ainda forem desejados, respeitando o máximo de cinco. Se usa defaults da agenda, consulte calendar_list_calendars para obter os valores. Não repita method + minutes.","items":{"type":"object","properties":{"method":{"type":"string","enum":["popup","email"]},"minutes":{"type":"integer","minimum":0,"maximum":40320}},"required":["method","minutes"],"additionalProperties":false}}
        },"additionalProperties":false}
        """)!;
        schema["required"] = create ? new System.Text.Json.Nodes.JsonArray("summary", "start", "end", "allDay") : new System.Text.Json.Nodes.JsonArray("eventId");
        schema["properties"]!["calendarId"] = new System.Text.Json.Nodes.JsonObject
        {
            ["type"] = new System.Text.Json.Nodes.JsonArray("string", "null"),
            ["description"] = create
                ? "Omitido/null: usa primary. Outro ID somente se o usuário pediu explicitamente esse destino e ele foi observado em calendar_list_calendars ou eventos. Nunca escolha automaticamente outra agenda; esclareça nomes ambíguos."
                : "ID do calendário de origem observado. Omitido: backend resolve se eventId for único no contexto recente."
        };
        if (!create) schema["properties"]!["eventId"] = new System.Text.Json.Nodes.JsonObject { ["type"] = "string", ["description"] = "ID observado em tools Calendar recentes desta conversa." };
        if (create) schema["properties"]!["recurrence"] = RecurrenceSchema(false);
        return JsonSerializer.SerializeToElement(schema);
    }

    private static System.Text.Json.Nodes.JsonObject RecurrenceSchema(bool allowNone)
    {
        var schema = System.Text.Json.Nodes.JsonNode.Parse("""
        {"type":"object","properties":{
          "frequency":{"type":"string","enum":["daily","weekly","monthly","yearly"],"description":"daily, weekly, monthly ou yearly. Quinzenal: weekly com interval=2."},
          "interval":{"type":"integer","minimum":1,"description":"A cada quantos dias/semanas/meses/anos; omitido equivale a 1."},
          "daysOfWeek":{"type":"array","minItems":1,"maxItems":7,"uniqueItems":true,"items":{"type":"string","enum":["monday","tuesday","wednesday","thursday","friday","saturday","sunday"]},"description":"Só weekly: dias da semana. Omitido usa o dia da primeira ocorrência. Inclua o dia da primeira ocorrência."},
          "count":{"type":"integer","minimum":1,"description":"Quantidade total de ocorrências; não combine com until."},
          "until":{"type":"string","description":"Última data INCLUSIVA YYYY-MM-DD no timezone do evento; não combine com count."}
        },"required":["frequency"],"additionalProperties":false}
        """)!.AsObject();
        if (allowNone) schema["properties"]!["frequency"]!["enum"] = new System.Text.Json.Nodes.JsonArray("none", "daily", "weekly", "monthly", "yearly");
        return schema;
    }

    internal static JsonElement PendingMutationSchema()
    {
        var schema = System.Text.Json.Nodes.JsonNode.Parse(MutationSchema(false).GetRawText())!;
        schema["properties"]!.AsObject().Remove("eventId");
        schema["properties"]!["recurrence"] = RecurrenceSchema(true);
        schema["properties"]!["calendarId"]!["description"] = "Omitido: preserva o calendário da proposta. Outra agenda somente para criação pendente, se explicitamente solicitada e observada.";
        schema["properties"]!["description"]!["description"] = "Anotação da proposta: omita para preservar, envie texto para substituir ou string vazia para limpar; não use null. Para acrescentar, envie o conteúdo completo preservando a anotação já apresentada na conversa. Somente conteúdo solicitado pelo usuário, sem metadata interna.";
        schema["required"] = new System.Text.Json.Nodes.JsonArray();
        schema["minProperties"] = 1;
        return JsonSerializer.SerializeToElement(schema);
    }
}

public sealed class CalendarCreateEventTool(IAegisDbContext db, ICalendarService calendar, CalendarToolContextService toolContext) : CalendarPrepareActionTool(db, calendar, toolContext)
{
    public override string Name => "calendar_create_event";
    protected override string ActionType => CalendarActionTypes.Create;
    public override string Description => "Prepara criação de evento na agenda, único ou recorrente, com título, início e fim concretos da PRIMEIRA ocorrência; sua duração vale para a série. Para 'toda segunda, quarta e sexta', use recurrence={frequency:weekly,daysOfWeek:[monday,wednesday,friday]}; quinzenal usa weekly + interval=2; mensal e anual repetem a data da primeira ocorrência. count ou until (data inclusiva YYYY-MM-DD) são opcionais e exclusivos; sem ambos, a série não tem término. Omitir recurrence cria evento único. Uma rotina concreta informada com dias e horários, como 'tenho aula toda terça e quinta das 13h20 às 15h', pode ser preparada para confirmação. Para compromisso recorrente com hora e sem duração, escolha duração razoável, por exemplo uma hora, e apresente-a. Pedido apenas para lembrar em um horário, sem compromisso/agenda, pertence às tools Reminder; elas não criam séries Calendar. Usa primary por padrão. O nome simples da atividade já serve como summary: 'cria uma reunião' pode usar 'Reunião', sem pedir título elaborado. description é anotação opcional: só envie quando solicitada. Sem preferência de lembretes, omita reminderMode/reminders para usar a policy Aegis; pedidos explícitos podem usar custom, none ou calendar_default. Outro calendário somente quando o usuário indicar o destino; resolva calendarId. Se o usuário delegou título, horário ou duração, escolha valores razoáveis e apresente-os; esclareça ausências essenciais sem delegação. Correções da proposta usam calendar_amend_pending_action. Exige confirmação em turno posterior; não altera Google agora. holidayWarnings para série cobrem somente a primeira ocorrência; feriados não bloqueiam. Sem convidados ou Meet.";
    public override JsonElement ParametersSchema { get; } = MutationSchema(true);
}

public sealed class CalendarUpdateEventTool(IAegisDbContext db, ICalendarService calendar, CalendarToolContextService toolContext) : CalendarPrepareActionTool(db, calendar, toolContext)
{
    public override string Name => "calendar_update_event";
    protected override string ActionType => CalendarActionTypes.Update;
    public override string Description => "Prepara alteração de evento já existente no Google e observado no calendário de origem (calendarId + eventId), com confirmação posterior. Pode alterar apenas description: omita para preservar, envie o texto completo para definir/substituir ou string vazia para remover. Para acrescentar, leia a anotação atual e preserve seu conteúdo. Mudança de horário/título não deve gerar nem alterar anotação. Alertas relativos a evento identificado continuam Calendar, inclusive “me lembra uma semana antes também”; antecedência é configuração do evento, não um novo instante Reminder. Também altera só lembretes: reminderMode custom (lista completa, máximo cinco, popup/email, 0–40320 minutos), aegis_default, calendar_default ou none. Omitido/keep preserva reminders, inclusive ao mudar entre timed e all-day. Só altere reminders se o usuário pediu. Para adicionar um lembrete aos existentes, leia-os antes; não substitua silenciosamente os demais. Para uma proposta ainda não executada use calendar_amend_pending_action. Campos omitidos são preservados. Para mover o horário mantendo a duração, leia os dados atuais e forneça início e fim novos. Sob delegação explícita de um detalhe, escolha um valor razoável e apresente-o; sem delegação, esclareça dados essenciais ausentes. Ao mudar datas/horário, considere holidayWarnings antes da confirmação; feriados não bloqueiam. Substitui propostas sem efeitos externos; não altera séries recorrentes completas.";
    public override JsonElement ParametersSchema { get; } = MutationSchema(false);
}

public sealed class CalendarDeleteEventTool(IAegisDbContext db, ICalendarService calendar, CalendarToolContextService toolContext) : CalendarPrepareActionTool(db, calendar, toolContext)
{
    public override string Name => "calendar_delete_event";
    protected override string ActionType => CalendarActionTypes.Delete;
    public override string Description => "Prepara exclusão de evento observado no calendário de origem (calendarId + eventId); exige confirmação em turno posterior. Não exclui agora. Para desistir de uma ação preparada use calendar_cancel_pending_action.";
    public override JsonElement ParametersSchema { get; } = EventReferenceSchema;
}

public sealed class CalendarAmendPendingActionTool(IAegisDbContext db, ICalendarService calendar, CalendarToolContextService toolContext) : CalendarToolBase
{
    public override string Name => "calendar_amend_pending_action";
    public override string Description => "Altera os campos fornecidos da última proposta Calendar ainda não executada e preserva os omitidos, inclusive recurrence e reminders. Numa criação pendente, envie recurrence completa para adicionar ou substituir a regra; recurrence={frequency:none} remove a recorrência e volta a evento único. Exemplo: para acrescentar quarta à segunda, envie weekly com daysOfWeek=[monday,wednesday]. Recurrence não se aplica à alteração de evento Google já existente. description substitui a anotação; string vazia remove. Criação pendente com policy Aegis muda para a policy all-day/timed adequada ao novo tipo; custom permanece preservado. reminderMode/reminders revisam a preferência. A nova proposta exige confirmação posterior e não altera Google agora. Para mover data/horário mantendo duração, forneça início e fim novos. Calendário alternativo somente quando solicitado e observado; alteração existente mantém sua origem. holidayWarnings para série cobrem só a primeira ocorrência. Recusa ações com possíveis efeitos externos.";
    public override JsonElement ParametersSchema { get; } = CalendarPrepareActionTool.PendingMutationSchema();

    protected override async Task<AegisToolResult> RunAsync(JsonElement arguments, ToolExecutionContext context, CancellationToken cancellationToken)
    {
        if (!arguments.EnumerateObject().Any()) return Error("invalid_calendar_tool_arguments", "Informe pelo menos um campo da proposta a alterar.");
        var previous = await db.GetLatestOpenPendingCalendarActionAsync(context.ConversationId, cancellationToken);
        if (previous is null) return Error("no_pending_calendar_action", "Não há proposta Calendar pendente válida nesta conversa.");
        if (previous.MayHaveAppliedChanges) return Error("calendar_action_outcome_unknown", "Esta ação pode ter alterado a agenda. Verifique o estado externo antes de preparar outra proposta.");
        if (previous.ActionType == CalendarActionTypes.Delete) return Error("invalid_calendar_tool_arguments", "Uma proposta de exclusão não possui campos editáveis. Prepare a nova ação desejada para substituí-la.");
        var payload = JsonSerializer.Deserialize<CalendarActionPayload>(previous.PayloadJson, JsonOptions)!;
        var fields = payload.Fields;
        string? Text(string key) => fields[key]?.GetValue<string>();
        var allDay = fields["start"] is null ? (bool?)null : fields["start"]?["date"] is not null;
        var end = allDay == true ? DateOnly.Parse(fields["end"]!["date"]!.GetValue<string>()).AddDays(-1).ToString("yyyy-MM-dd")
            : fields["end"]?["dateTime"]?.GetValue<string>();
        var original = new CalendarEventChanges(Text("summary"), fields["start"]?["date"]?.GetValue<string>() ?? fields["start"]?["dateTime"]?.GetValue<string>(),
            end, allDay, fields["start"]?["timeZone"]?.GetValue<string>(), Text("description"), Text("location"));
        var values = arguments.EnumerateObject().Where(p => p.Name != "calendarId").ToDictionary(p => p.Name, p => p.Value);
        var requested = JsonSerializer.SerializeToElement(values).Deserialize<CalendarEventChanges>(JsonOptions)!;
        if (requested.Recurrence is not null && previous.ActionType != CalendarActionTypes.Create)
            return Error("invalid_calendar_tool_arguments", "Recorrência só pode ser alterada numa criação ainda pendente.");
        if (requested.Reminders is not null && requested.ReminderMode != "custom")
            return Error("invalid_calendar_tool_arguments", "Informe reminderMode=custom junto da lista reminders.");
        var storedReminders = fields["reminders"]?.Deserialize<CalendarRemindersData>(JsonOptions);
        string StoredMode() => storedReminders is null ? (previous.ActionType == CalendarActionTypes.Create ? "aegis_default" : "keep")
            : storedReminders.UseDefault ? "calendar_default" : storedReminders.Overrides?.Count == 0 ? "none" : "custom";
        var reminderMode = requested.ReminderMode ?? payload.ReminderMode ?? StoredMode();
        var reminders = requested.ReminderMode is not null ? requested.Reminders
            : reminderMode == "custom" ? storedReminders?.Overrides : null;
        if (requested.ReminderMode == "keep" && previous.ActionType == CalendarActionTypes.Create)
        {
            reminderMode = StoredMode();
            reminders = reminderMode == "custom" ? storedReminders?.Overrides : null;
        }
        var changes = new CalendarEventChanges(requested.Summary ?? original.Summary, requested.Start ?? original.Start,
            requested.End ?? original.End, requested.AllDay ?? original.AllDay, requested.TimeZone ?? (requested.AllDay == true ? null : original.TimeZone),
            requested.Description ?? original.Description, requested.Location ?? original.Location, reminderMode, reminders,
            requested.Recurrence ?? payload.Recurrence);
        var calendarId = OptionalCalendarId(arguments) ?? previous.CalendarId;
        if (previous.ActionType == CalendarActionTypes.Create)
            await toolContext.RequireCalendarAsync(context.ConversationId, calendarId, cancellationToken);
        else
        {
            if (calendarId != previous.CalendarId) return Error("invalid_calendar_tool_arguments", "Alteração deve manter o calendário de origem do evento.");
            await toolContext.ResolveEventAsync(context.ConversationId, previous.EventId, calendarId, cancellationToken);
        }
        var id = previous.ActionType == CalendarActionTypes.Create ? Guid.NewGuid().ToString("N") : previous.EventId;
        return await CalendarPrepareActionTool.PrepareAndPersistAsync(db, calendar, previous.ActionType, calendarId, id, changes, context, cancellationToken);
    }
}

public sealed class CalendarConfirmPendingActionTool(IAegisDbContext db, ICalendarService calendar, CalendarToolContextService toolContext,
    ILogger<CalendarConfirmPendingActionTool> logger) : CalendarToolBase
{
    public override string Name => "calendar_confirm_pending_action";
    public override string Description => "Executa a última ação pendente Calendar quando a mensagem atual aceita a proposta apresentada em turno anterior. Interprete a intenção pelo contexto e linguagem natural; não exige frase específica. Correções da proposta usam calendar_amend_pending_action; desistência usa calendar_cancel_pending_action. O backend valida conversa, turno posterior, expiração e estado. Verifica o estado final no Google; retry usa o mesmo ID e não cria duplicatas.";
    public override JsonElement ParametersSchema { get; } = Schema("""{"type":"object","properties":{},"additionalProperties":false}""");
    protected override async Task<AegisToolResult> RunAsync(JsonElement arguments, ToolExecutionContext context, CancellationToken cancellationToken)
    {
        RequireEmpty(arguments);
        var action = await db.GetLatestOpenPendingCalendarActionAsync(context.ConversationId, cancellationToken);
        if (action is null) return Error("no_pending_calendar_action", "Não há ação Calendar pendente válida nesta conversa.");
        if (action.ConversationId != context.ConversationId || !action.IsOpen() ||
            !await PendingActionGuard.HasValidUserMessageAsync(db, context, action.CreatedAt, cancellationToken))
            return Error("confirmation_required", "Apresente a ação antes de uma confirmação em outro turno.");
        var payload = JsonSerializer.Deserialize<CalendarActionPayload>(action.PayloadJson, JsonOptions)!;
        var confirmedAt = DateTimeOffset.UtcNow;
        var previousPossibleEffects = action.MayHaveAppliedChanges;
        var requestMayHaveBeenSent = false;
        try
        {
            // Persist uncertainty before external execution, including process interruption after send.
            action.RecordPossibleExternalEffects();
            await db.SaveChangesAsync(cancellationToken);
            CalendarMutationException? failure = null;
            try
            {
                await calendar.ExecuteAsync(action.ActionType, action.CalendarId, action.EventId, payload, cancellationToken);
                requestMayHaveBeenSent = true;
            }
            catch (CalendarMutationCancelledException e) when (cancellationToken.IsCancellationRequested)
            { requestMayHaveBeenSent = e.RequestWasSent; throw; }
            catch (CalendarMutationException e) { failure = e; requestMayHaveBeenSent = e.RequestWasSent; }
            if (failure is { RequestWasSent: false })
            {
                if (!previousPossibleEffects) action.ClearPossibleExternalEffects();
                db.AddCalendarActionAudit(new(action.Id, context.ConversationId, action.ActionType, action.EventId,
                    context.UserMessageId, false, failure.Code, action.CalendarId));
                await db.SaveChangesAsync(cancellationToken);
                return Error(failure.Code, failure.Message + " A ação continua aberta; efeitos anteriores não foram revertidos.");
            }
            bool verified;
            try { verified = await calendar.VerifyAsync(action.ActionType, action.CalendarId, action.EventId, payload, cancellationToken); }
            catch (CalendarException) { verified = false; }
            catch (HttpRequestException) { verified = false; }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { verified = false; }
            if (verified)
            {
                cancellationToken.ThrowIfCancellationRequested();
                action.Confirm(confirmedAt);
                action.MarkExecuted();
                db.AddCalendarActionAudit(new(action.Id, context.ConversationId, action.ActionType, action.EventId,
                    context.UserMessageId, true, failure is null ? null : "Estado confirmado após falha da requisição.", action.CalendarId));
                await db.SaveChangesAsync(cancellationToken);
                CalendarEventData? observedEvent = null;
                if (action.ActionType != CalendarActionTypes.Delete)
                {
                    // Context bookkeeping failure must not turn a verified mutation into a reported failure.
                    try
                    {
                        observedEvent = await calendar.GetEventAsync(action.CalendarId, action.EventId, cancellationToken);
                        await toolContext.RememberAsync(context.ConversationId, [observedEvent], Name, cancellationToken);
                    }
                    catch (Exception e) when (e is not OperationCanceledException) { logger.LogWarning(e, "Could not remember verified Calendar event {EventId}", action.EventId); }
                }
                return Ok(new { pendingActionId = action.Id, calendarId = observedEvent?.CalendarId ?? action.CalendarId, action.EventId, action.HumanSummary, verified = true, recoveredAfterFailure = failure is not null });
            }
            db.AddCalendarActionAudit(new(action.Id, context.ConversationId, action.ActionType, action.EventId,
                context.UserMessageId, false, failure?.Code ?? "Estado final não confirmado.", action.CalendarId));
            await db.SaveChangesAsync(cancellationToken);
            return Error("calendar_action_outcome_unknown", "A requisição pode ter alterado a agenda, mas não confirmei o estado final. A ação continua aberta; confirmar novamente é seguro. Não afirme que nada mudou.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (!requestMayHaveBeenSent && !previousPossibleEffects) action.ClearPossibleExternalEffects();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            try
            {
                db.AddCalendarActionAudit(new(action.Id, context.ConversationId, action.ActionType, action.EventId,
                    context.UserMessageId, false, requestMayHaveBeenSent ? "Turno cancelado; possíveis efeitos externos." : "Turno cancelado; estado externo não confirmado.", action.CalendarId));
                await db.SaveChangesAsync(timeout.Token);
            }
            catch (Exception e) { logger.LogError(e, "Could not audit Calendar cancellation {ActionId}", action.Id); }
            throw;
        }
        catch (Exception e) when (requestMayHaveBeenSent)
        {
            logger.LogError(e, "Could not finish Calendar action bookkeeping {ActionId}", action.Id);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            try
            {
                db.AddCalendarActionAudit(new(action.Id, context.ConversationId, action.ActionType, action.EventId,
                    context.UserMessageId, false, "Possíveis efeitos externos; falha ao concluir registro/verificação.", action.CalendarId));
                await db.SaveChangesAsync(timeout.Token);
            }
            catch (Exception auditError) { logger.LogError(auditError, "Could not audit Calendar failure {ActionId}", action.Id); }
            return Error("calendar_action_outcome_unknown", "A agenda pode ter sido alterada, mas não consegui concluir o registro da ação. Consulte o evento antes de preparar outra ação; não afirme que nada mudou.");
        }
    }
}

public sealed class CalendarCancelPendingActionTool(IAegisDbContext db) : CalendarToolBase
{
    public override string Name => "calendar_cancel_pending_action";
    public override string Description => "Descarta a última proposta Calendar quando o usuário desiste, conforme intenção e contexto, sem exigir frase específica ou confirmação de cancelamento. Para corrigir a proposta use calendar_amend_pending_action diretamente. Não exclui um evento Google nem reverte efeitos de tentativas anteriores; possíveis efeitos externos continuam explícitos.";
    public override JsonElement ParametersSchema { get; } = Schema("""{"type":"object","properties":{},"additionalProperties":false}""");
    protected override async Task<AegisToolResult> RunAsync(JsonElement arguments, ToolExecutionContext context, CancellationToken cancellationToken)
    {
        RequireEmpty(arguments);
        if (!await PendingActionGuard.HasValidUserMessageAsync(db, context, cancellationToken: cancellationToken))
            return Error("invalid_calendar_tool_arguments", "A mensagem atual precisa pertencer a esta conversa.");
        var action = (await db.GetUnresolvedPendingCalendarActionsAsync(context.ConversationId, cancellationToken))
            .FirstOrDefault(a => a.IsOpen() || a.MayHaveAppliedChanges);
        if (action is null) return Error("no_pending_calendar_action", "Não há ação Calendar pendente válida nesta conversa.");
        action.Cancel();
        db.AddCalendarActionAudit(new(action.Id, context.ConversationId, "cancel_" + action.ActionType, action.EventId, context.UserMessageId, true, calendarId: action.CalendarId));
        await db.SaveChangesAsync(cancellationToken);
        return Ok(new { pendingActionId = action.Id, possibleExternalEffects = action.MayHaveAppliedChanges, userMessage = action.MayHaveAppliedChanges
            ? "Ação pendente cancelada. Possíveis alterações de tentativas anteriores não foram revertidas."
            : "Ação pendente cancelada antes da execução." });
    }
}
