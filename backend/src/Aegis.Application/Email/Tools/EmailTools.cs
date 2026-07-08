using System.Text.Json;
using System.Text.Json.Serialization;
using Aegis.Application.Common;
using Aegis.Application.Tools;
using Aegis.Domain;
using Aegis.Domain.Entities;

namespace Aegis.Application.Email.Tools;

public sealed class EmailGetStatusTool(IEmailConnectionService connectionService) : EmailToolBase
{
    public override string Name => "email_get_status";

    public override string Description => "Verifica se o Gmail está conectado à Aegis.";

    public override JsonElement ParametersSchema { get; } = Schema("""
        {
          "type": "object",
          "properties": {},
          "additionalProperties": false
        }
        """);

    public override async Task<AegisToolResult> ExecuteAsync(
        JsonElement arguments,
        ToolExecutionContext context,
        CancellationToken cancellationToken = default)
    {
        var status = await connectionService.GetStatusAsync(cancellationToken);
        return Ok(new { status });
    }
}

public sealed class EmailCreateConnectLinkTool(IEmailConnectionService connectionService) : EmailToolBase
{
    public override string Name => "email_create_connect_link";

    public override string Description => "Cria um link para Pedro autorizar a conexão com o Gmail.";

    public override JsonElement ParametersSchema { get; } = Schema("""
        {
          "type": "object",
          "properties": {},
          "additionalProperties": false
        }
        """);

    public override async Task<AegisToolResult> ExecuteAsync(
        JsonElement arguments,
        ToolExecutionContext context,
        CancellationToken cancellationToken = default)
    {
        var response = await connectionService.CreateAuthorizationUrlAsync(cancellationToken);
        return Ok(new
        {
            isConnected = false,
            authorizationUrl = response.AuthorizationUrl,
            userMessage = "Ainda não estou conectada ao Gmail. Use este link para autorizar o acesso."
        });
    }
}

public sealed class EmailSearchTool(
    IEmailService emailService,
    IEmailToolContextService emailContextService) : EmailToolBase
{
    public override string Name => "email_search";

    public override string Description =>
        "Busca emails no Gmail por consulta. Combine todas as restrições pedidas por Pedro na query: por exemplo, não lidos e importantes deve usar is:unread is:important; com estrela deve usar is:starred; último mês deve usar newer_than:30d.";

    public override JsonElement ParametersSchema { get; } = Schema("""
        {
          "type": "object",
          "properties": {
            "query": {
              "type": ["string", "null"],
              "description": "Consulta Gmail. Restrições são cumulativas: use is:unread is:important para emails não lidos e importantes, is:starred para com estrela, newer_than:30d para último mês, from:github para remetente e termos como Unicentro quando fizer sentido."
            },
            "limit": {
              "type": ["integer", "null"],
              "minimum": 1,
              "maximum": 50
            },
            "includeRead": {
              "type": ["boolean", "null"],
              "description": "Use false para restringir a não lidos; use true quando o pedido for geral."
            },
            "newerThanDays": {
              "type": ["integer", "null"],
              "minimum": 1,
              "maximum": 365
            }
          },
          "additionalProperties": false
        }
        """);

    public override async Task<AegisToolResult> ExecuteAsync(
        JsonElement arguments,
        ToolExecutionContext context,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var requestedLimit = GetInt(arguments, "limit");
            var searchResult = await emailService.SearchEmailsAsync(
                GetString(arguments, "query"),
                requestedLimit,
                GetBool(arguments, "includeRead"),
                GetInt(arguments, "newerThanDays"),
                cancellationToken);
            await emailContextService.RememberSearchAsync(
                context.ConversationId,
                searchResult.Emails,
                Name,
                cancellationToken);
            var returnedCount = searchResult.Emails.Count;
            var totalMatchingCount = searchResult.TotalMatchingCount;
            return Ok(new
            {
                emails = searchResult.Emails,
                totalMatchingCount,
                requestedLimit = requestedLimit.GetValueOrDefault(),
                requestedLimitProvided = requestedLimit.HasValue,
                returnedCount,
                limitReached = requestedLimit.HasValue && returnedCount >= requestedLimit.Value,
                auditMessage = $"A busca retornou {returnedCount} email{(returnedCount == 1 ? string.Empty : "s")} de um total de {totalMatchingCount} que correspondem à query."
            });
        }
        catch (EmailNotConnectedException)
        {
            return Error("email_not_connected", "Gmail is not connected. Call email_create_connect_link before answering.");
        }
    }
}

public sealed class EmailReadTool(
    IEmailService emailService,
    IEmailToolContextService emailContextService) : EmailToolBase
{
    public override string Name => "email_read";

    public override string Description => "Lê o corpo de um email específico pelo id retornado por email_search.";

    public override JsonElement ParametersSchema { get; } = Schema("""
        {
          "type": "object",
          "properties": {
            "emailId": { "type": "string" },
            "readPurpose": {
              "type": ["string", "null"],
              "enum": ["briefing", "full", null],
              "description": "Use briefing para triagem/resumo rápido de inbox; use full quando Pedro pedir para ler, descrever, especificar ou explicar melhor um email."
            }
          },
          "required": ["emailId"],
          "additionalProperties": false
        }
        """);

    public override async Task<AegisToolResult> ExecuteAsync(
        JsonElement arguments,
        ToolExecutionContext context,
        CancellationToken cancellationToken = default)
    {
        var emailId = GetRequiredString(arguments, "emailId");
        if (emailId is null)
        {
            return Error("invalid_arguments", "emailId is required.");
        }

        try
        {
            var email = await emailService.ReadEmailAsync(
                emailId,
                GetReadPurpose(arguments),
                cancellationToken);
            await emailContextService.RememberEmailAsync(
                context.ConversationId,
                email,
                Name,
                cancellationToken);
            return Ok(new { email });
        }
        catch (EmailNotConnectedException)
        {
            return Error("email_not_connected", "Gmail is not connected. Call email_create_connect_link before answering.");
        }
    }
}

public sealed class EmailReadThreadTool(
    IEmailService emailService,
    IEmailToolContextService emailContextService) : EmailToolBase
{
    public override string Name => "email_read_thread";

    public override string Description => "Lê uma thread específica pelo threadId retornado por email_search ou email_read.";

    public override JsonElement ParametersSchema { get; } = Schema("""
        {
          "type": "object",
          "properties": {
            "threadId": { "type": "string" },
            "readPurpose": {
              "type": ["string", "null"],
              "enum": ["briefing", "full", null],
              "description": "Use briefing para triagem/resumo rápido de inbox; use full quando Pedro pedir para ler, descrever, especificar ou explicar melhor uma conversa/thread."
            }
          },
          "required": ["threadId"],
          "additionalProperties": false
        }
        """);

    public override async Task<AegisToolResult> ExecuteAsync(
        JsonElement arguments,
        ToolExecutionContext context,
        CancellationToken cancellationToken = default)
    {
        var threadId = GetRequiredString(arguments, "threadId");
        if (threadId is null)
        {
            return Error("invalid_arguments", "threadId is required.");
        }

        try
        {
            var thread = await emailService.ReadThreadAsync(
                threadId,
                GetReadPurpose(arguments),
                cancellationToken);
            await emailContextService.RememberThreadAsync(
                context.ConversationId,
                thread,
                Name,
                cancellationToken);
            return Ok(new { thread });
        }
        catch (EmailNotConnectedException)
        {
            return Error("email_not_connected", "Gmail is not connected. Call email_create_connect_link before answering.");
        }
    }
}

public sealed class EmailMarkReadTool(
    IAegisDbContext dbContext,
    IEmailToolContextService emailContextService,
    IEmailService emailService) : PendingEmailModificationTool(dbContext, emailContextService, emailService)
{
    public override string Name => "email_mark_read";
    protected override string ActionType => EmailActionTypes.MarkRead;
    protected override string Verb => "marcar como lidos";
}

public sealed class EmailMarkUnreadTool(
    IAegisDbContext dbContext,
    IEmailToolContextService emailContextService,
    IEmailService emailService) : PendingEmailModificationTool(dbContext, emailContextService, emailService)
{
    public override string Name => "email_mark_unread";
    protected override string ActionType => EmailActionTypes.MarkUnread;
    protected override string Verb => "marcar como não lidos";
}

public sealed class EmailStarTool(
    IAegisDbContext dbContext,
    IEmailToolContextService emailContextService,
    IEmailService emailService) : PendingEmailModificationTool(dbContext, emailContextService, emailService)
{
    public override string Name => "email_star";
    protected override string ActionType => EmailActionTypes.Star;
    protected override string Verb => "estrelar";
}

public sealed class EmailUnstarTool(
    IAegisDbContext dbContext,
    IEmailToolContextService emailContextService,
    IEmailService emailService) : PendingEmailModificationTool(dbContext, emailContextService, emailService)
{
    public override string Name => "email_unstar";
    protected override string ActionType => EmailActionTypes.Unstar;
    protected override string Verb => "remover estrela de";
}

public sealed class EmailMarkImportantTool(
    IAegisDbContext dbContext,
    IEmailToolContextService emailContextService,
    IEmailService emailService) : PendingEmailModificationTool(dbContext, emailContextService, emailService)
{
    public override string Name => "email_mark_important";
    protected override string ActionType => EmailActionTypes.MarkImportant;
    protected override string Verb => "marcar como importantes";
}

public sealed class EmailUnmarkImportantTool(
    IAegisDbContext dbContext,
    IEmailToolContextService emailContextService,
    IEmailService emailService) : PendingEmailModificationTool(dbContext, emailContextService, emailService)
{
    public override string Name => "email_unmark_important";
    protected override string ActionType => EmailActionTypes.UnmarkImportant;
    protected override string Verb => "remover importante de";
}

public sealed class EmailConfirmPendingActionTool(
    IAegisDbContext dbContext,
    IEmailService emailService,
    IEmailToolContextService emailContextService) : EmailToolBase
{
    public override string Name => "email_confirm_pending_action";

    public override string Description =>
        "Confirma a última ação pendente de email quando a mensagem atual de Pedro for uma confirmação clara em linguagem natural. A interpretação da confirmação é feita pelo modelo; o backend valida que existe ação pendente aberta e que a mensagem não é uma recusa clara.";

    public override JsonElement ParametersSchema { get; } = Schema("""
        {
          "type": "object",
          "properties": {},
          "additionalProperties": false
        }
        """);

    public override async Task<AegisToolResult> ExecuteAsync(
        JsonElement arguments,
        ToolExecutionContext context,
        CancellationToken cancellationToken = default)
    {
        if (LooksLikeCancellation(context.UserContent))
        {
            return Error("ambiguous_confirmation", "The current user message looks like a cancellation, not a confirmation. Do not execute.");
        }

        var action = await dbContext.GetLatestOpenPendingEmailActionAsync(context.ConversationId, cancellationToken);
        if (action is null)
        {
            return Error("no_pending_action", "There is no open pending email action for this conversation.");
        }

        var emailIds = DeserializeEmailIds(action.EmailIdsJson);
        try
        {
            var result = await ExecuteModificationAsync(emailService, action.ActionType, emailIds, cancellationToken);
            var verification = await VerifyModificationAsync(
                emailService,
                emailContextService,
                context.ConversationId,
                action.ActionType,
                emailIds,
                Name,
                cancellationToken);
            action.Confirm();
            action.MarkExecuted();
            dbContext.AddEmailActionAudit(new EmailActionAudit(
                context.ConversationId,
                action.ActionType,
                action.EmailIdsJson,
                context.UserMessageId,
                success: verification.AllConfirmed));
            await dbContext.SaveChangesAsync(cancellationToken);

            return Ok(new
            {
                pendingActionId = action.Id,
                action.ActionType,
                action.HumanSummary,
                result,
                verification,
                userMessage = verification.AllConfirmed
                    ? $"Pronto, concluí: {action.HumanSummary}."
                    : $"Executei a ação, mas a verificação não confirmou todos os emails: {action.HumanSummary}."
            });
        }
        catch (Exception exception)
        {
            dbContext.AddEmailActionAudit(new EmailActionAudit(
                context.ConversationId,
                action.ActionType,
                action.EmailIdsJson,
                context.UserMessageId,
                success: false,
                exception.Message));
            await dbContext.SaveChangesAsync(cancellationToken);

            return Error("email_action_failed", "The pending email action failed while executing.");
        }
    }

    public static Task<EmailModificationResult> ExecuteModificationAsync(
        IEmailService emailService,
        string actionType,
        IReadOnlyList<string> emailIds,
        CancellationToken cancellationToken)
    {
        return actionType switch
        {
            EmailActionTypes.MarkRead => emailService.MarkReadAsync(emailIds, cancellationToken),
            EmailActionTypes.MarkUnread => emailService.MarkUnreadAsync(emailIds, cancellationToken),
            EmailActionTypes.Star => emailService.StarAsync(emailIds, cancellationToken),
            EmailActionTypes.Unstar => emailService.UnstarAsync(emailIds, cancellationToken),
            EmailActionTypes.MarkImportant => emailService.MarkImportantAsync(emailIds, cancellationToken),
            EmailActionTypes.UnmarkImportant => emailService.UnmarkImportantAsync(emailIds, cancellationToken),
            _ => throw new InvalidOperationException($"Unknown email action '{actionType}'.")
        };
    }

    public static async Task<EmailModificationVerification> VerifyModificationAsync(
        IEmailService emailService,
        IEmailToolContextService emailContextService,
        Guid conversationId,
        string actionType,
        IReadOnlyList<string> emailIds,
        string sourceToolName,
        CancellationToken cancellationToken)
    {
        var emails = new List<EmailContentData>();
        foreach (var emailId in emailIds)
        {
            emails.Add(await emailService.ReadEmailAsync(
                emailId,
                EmailBodyReadPurpose.Full,
                cancellationToken));
        }

        foreach (var email in emails)
        {
            await emailContextService.RememberEmailAsync(
                conversationId,
                email,
                sourceToolName,
                cancellationToken);
        }

        var failedIds = emails
            .Where(email => !IsExpectedState(actionType, email))
            .Select(email => email.Id)
            .ToList();

        return new EmailModificationVerification(
            failedIds.Count == 0,
            emails.Count,
            failedIds);
    }

    private static bool IsExpectedState(string actionType, EmailContentData email)
    {
        return actionType switch
        {
            EmailActionTypes.MarkRead => !email.IsUnread,
            EmailActionTypes.MarkUnread => email.IsUnread,
            EmailActionTypes.Star => email.IsStarred,
            EmailActionTypes.Unstar => !email.IsStarred,
            EmailActionTypes.MarkImportant => email.IsImportant,
            EmailActionTypes.UnmarkImportant => !email.IsImportant,
            _ => false
        };
    }
}

public sealed record EmailModificationVerification(
    bool AllConfirmed,
    int CheckedCount,
    IReadOnlyList<string> FailedEmailIds);

public sealed class EmailCancelPendingActionTool(IAegisDbContext dbContext) : EmailToolBase
{
    public override string Name => "email_cancel_pending_action";

    public override string Description => "Cancela por texto a última ação pendente de email da conversa sem modificar nada.";

    public override JsonElement ParametersSchema { get; } = Schema("""
        {
          "type": "object",
          "properties": {},
          "additionalProperties": false
        }
        """);

    public override async Task<AegisToolResult> ExecuteAsync(
        JsonElement arguments,
        ToolExecutionContext context,
        CancellationToken cancellationToken = default)
    {
        if (!LooksLikeCancellation(context.UserContent))
        {
            return Error("ambiguous_cancellation", "The current user message is not a clear textual cancellation. Ask Pedro to confirm more clearly.");
        }

        var action = await dbContext.GetLatestOpenPendingEmailActionAsync(context.ConversationId, cancellationToken);
        if (action is null)
        {
            return Error("no_pending_action", "There is no open pending email action for this conversation.");
        }

        action.Cancel();
        dbContext.AddEmailActionAudit(new EmailActionAudit(
            context.ConversationId,
            $"cancel_{action.ActionType}",
            action.EmailIdsJson,
            context.UserMessageId,
            success: true));
        await dbContext.SaveChangesAsync(cancellationToken);

        return Ok(new
        {
            pendingActionId = action.Id,
            action.ActionType,
            action.HumanSummary,
            userMessage = "Não mexi em nada."
        });
    }
}

public abstract class PendingEmailModificationTool(
    IAegisDbContext dbContext,
    IEmailToolContextService emailContextService,
    IEmailService emailService) : EmailToolBase
{
    public override string Description =>
        $"Cria uma ação pendente para {Verb} emails em batch. Passe todos os emailIds selecionados em uma única chamada sempre que possível. Não executa a modificação; exige confirmação textual posterior.";

    public override JsonElement ParametersSchema { get; } = Schema("""
        {
          "type": "object",
          "properties": {
            "emailIds": {
              "type": "array",
              "items": { "type": "string" },
              "maxItems": 100
            },
            "selectionKey": {
              "type": ["string", "null"],
              "description": "Chave de seleção observada pelo backend, como last_search ou last_modified_attempt. Use last_search apenas quando Pedro pedir todos os emails da última busca, não quando ele se referir só aos itens resumidos/citados."
            },
            "humanSummary": {
              "type": ["string", "null"],
              "description": "Resumo natural em português do que será confirmado, sem ids técnicos."
            }
          },
          "additionalProperties": false
        }
        """);

    protected abstract string ActionType { get; }

    protected abstract string Verb { get; }

    public override async Task<AegisToolResult> ExecuteAsync(
        JsonElement arguments,
        ToolExecutionContext context,
        CancellationToken cancellationToken = default)
    {
        var emailIds = GetStringArray(arguments, "emailIds")
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToList();
        var selectionKey = GetString(arguments, "selectionKey");
        var resolution = await emailContextService.ResolveAsync(
            context.ConversationId,
            emailIds,
            selectionKey,
            cancellationToken);
        if (!resolution.IsValid)
        {
            return RecoverableArgumentError(new
            {
                error = "invalid_email_tool_arguments",
                message = resolution.ErrorMessage,
                invalidEmailIds = resolution.InvalidEmailIds,
                availableSelectionKeys = resolution.AvailableSelectionKeys,
                instruction = "Refaça a chamada usando apenas emailIds observados no contexto recente ou uma selectionKey disponível. Nunca use placeholders."
            });
        }

        var humanSummary = GetString(arguments, "humanSummary");
        if (string.IsNullOrWhiteSpace(humanSummary))
        {
            humanSummary = $"{Verb} {resolution.EmailIds.Count} email{(resolution.EmailIds.Count == 1 ? string.Empty : "s")}";
        }

        await emailContextService.RememberModifiedAttemptAsync(
            context.ConversationId,
            resolution.EmailIds,
            humanSummary,
            Name,
            cancellationToken);

        var emailIdsJson = JsonSerializer.Serialize(resolution.EmailIds, JsonOptions);
        var pendingAction = new PendingEmailAction(
            context.ConversationId,
            ActionType,
            emailIdsJson,
            humanSummary,
            DateTimeOffset.UtcNow.AddMinutes(10));
        dbContext.AddPendingEmailAction(pendingAction);

        if (LooksLikeInlineConfirmation(context.UserContent))
        {
            try
            {
                var result = await EmailConfirmPendingActionTool.ExecuteModificationAsync(
                    emailService,
                    ActionType,
                    resolution.EmailIds,
                    cancellationToken);
                var verification = await EmailConfirmPendingActionTool.VerifyModificationAsync(
                    emailService,
                    emailContextService,
                    context.ConversationId,
                    ActionType,
                    resolution.EmailIds,
                    Name,
                    cancellationToken);
                pendingAction.Confirm();
                pendingAction.MarkExecuted();
                dbContext.AddEmailActionAudit(new EmailActionAudit(
                    context.ConversationId,
                    ActionType,
                    emailIdsJson,
                    context.UserMessageId,
                    success: verification.AllConfirmed));
                await dbContext.SaveChangesAsync(cancellationToken);

                return Ok(new
                {
                    pendingActionId = pendingAction.Id,
                    pendingAction.ActionType,
                    pendingAction.HumanSummary,
                    result,
                    verification,
                    userMessage = verification.AllConfirmed
                        ? $"Pronto, concluí: {pendingAction.HumanSummary}."
                        : $"Executei a ação, mas a verificação não confirmou todos os emails: {pendingAction.HumanSummary}."
                });
            }
            catch (Exception exception)
            {
                dbContext.AddEmailActionAudit(new EmailActionAudit(
                    context.ConversationId,
                    ActionType,
                    emailIdsJson,
                    context.UserMessageId,
                    success: false,
                    exception.Message));
                await dbContext.SaveChangesAsync(cancellationToken);

                return Error("email_action_failed", "The email action failed while executing.");
            }
        }

        await dbContext.SaveChangesAsync(cancellationToken);

        return Ok(new
        {
            pendingActionId = pendingAction.Id,
            pendingAction.ActionType,
            pendingAction.HumanSummary,
            emailCount = resolution.EmailIds.Count,
            selectionKey = resolution.SelectionKey,
            pendingAction.ExpiresAt,
            userMessage = $"Posso {humanSummary} se você confirmar por texto."
        });
    }
}

public abstract class EmailToolBase : IAegisTool
{
    protected static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public abstract string Name { get; }

    public abstract string Description { get; }

    public abstract JsonElement ParametersSchema { get; }

    public abstract Task<AegisToolResult> ExecuteAsync(
        JsonElement arguments,
        ToolExecutionContext context,
        CancellationToken cancellationToken = default);

    protected static AegisToolResult Ok(object payload)
    {
        return new AegisToolResult(true, JsonSerializer.Serialize(payload, JsonOptions));
    }

    protected static AegisToolResult Error(string code, string message)
    {
        return new AegisToolResult(
            false,
            JsonSerializer.Serialize(new { error = code, message }, JsonOptions),
            code);
    }

    protected static AegisToolResult RecoverableArgumentError(object payload)
    {
        return new AegisToolResult(
            false,
            JsonSerializer.Serialize(payload, JsonOptions),
            "invalid_tool_arguments");
    }

    protected static JsonElement Schema(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    protected static string? GetRequiredString(JsonElement arguments, string propertyName)
    {
        var value = GetString(arguments, propertyName);
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    protected static string? GetString(JsonElement arguments, string propertyName)
    {
        if (!arguments.TryGetProperty(propertyName, out var property) ||
            property.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return null;
        }

        return property.ValueKind == JsonValueKind.String
            ? property.GetString()?.Trim()
            : null;
    }

    protected static EmailBodyReadPurpose GetReadPurpose(JsonElement arguments)
    {
        return string.Equals(GetString(arguments, "readPurpose"), "briefing", StringComparison.OrdinalIgnoreCase)
            ? EmailBodyReadPurpose.Briefing
            : EmailBodyReadPurpose.Full;
    }

    protected static int? GetInt(JsonElement arguments, string propertyName)
    {
        if (!arguments.TryGetProperty(propertyName, out var property) ||
            property.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return null;
        }

        return property.ValueKind == JsonValueKind.Number && property.TryGetInt32(out var value)
            ? value
            : null;
    }

    protected static bool? GetBool(JsonElement arguments, string propertyName)
    {
        if (!arguments.TryGetProperty(propertyName, out var property) ||
            property.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return null;
        }

        return property.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => null
        };
    }

    protected static IReadOnlyList<string> GetStringArray(JsonElement arguments, string propertyName)
    {
        if (!arguments.TryGetProperty(propertyName, out var property) ||
            property.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return property.EnumerateArray()
            .Where(item => item.ValueKind == JsonValueKind.String)
            .Select(item => item.GetString())
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value!.Trim())
            .ToList();
    }

    protected static IReadOnlyList<string> DeserializeEmailIds(string emailIdsJson)
    {
        return JsonSerializer.Deserialize<IReadOnlyList<string>>(emailIdsJson, JsonOptions) ?? [];
    }

    protected static bool LooksLikeConfirmation(string userContent)
    {
        var normalized = NormalizeIntent(userContent);
        return normalized is "sim"
            or "pode"
            or "faz"
            or "confirmo"
            or "confirma"
            or "ok"
            or "okay"
            or "beleza"
            or "manda"
            or "pode sim"
            or "sim pode"
            or "isso"
            or "confirmado" ||
            normalized.Contains("manda bala", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("pode fazer", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("pode marcar", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("pode executar", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("sim pode", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("sim, pode", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("sim confirma", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("sim, confirma", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("confirmo", StringComparison.OrdinalIgnoreCase);
    }

    protected static bool LooksLikeInlineConfirmation(string userContent)
    {
        var normalized = NormalizeIntent(userContent);
        return LooksLikeConfirmation(normalized) ||
            normalized.Contains("manda bala", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("pode fazer", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("pode marcar", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("sim pode", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("sim, pode", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("sim confirma", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("sim, confirma", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("ja confirmo", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("já confirmo", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("pode executar", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("quero essa ação", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("quero essa acao", StringComparison.OrdinalIgnoreCase);
    }

    protected static bool LooksLikeCancellation(string userContent)
    {
        var normalized = NormalizeIntent(userContent);
        return normalized is "nao"
            or "não"
            or "cancela"
            or "cancelar"
            or "cancele"
            or "nao mexe"
            or "não mexe"
            or "deixa"
            or "deixa quieto" ||
            normalized.StartsWith("nao ", StringComparison.OrdinalIgnoreCase) ||
            normalized.StartsWith("não ", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains(" cancela", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains(" cancele", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains(" cancelar", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("nao mexe", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("não mexe", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("deixa quieto", StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeIntent(string value)
    {
        return value
            .Trim()
            .Trim('.', '!', '?', ',', ';', ':')
            .ToLowerInvariant();
    }
}
