using Aegis.Application.Models;
using Aegis.Application.Llm;
using Aegis.Application.Observability;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace Aegis.Application.Tools;

public sealed class AegisToolLoop(
    IAegisModelClient modelClient,
    IAegisToolRegistry toolRegistry,
    ILogger<AegisToolLoop> logger,
    AegisMetrics metrics) : IAegisToolLoop
{
    private const int DefaultMaxIterations = 4;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public async Task<ModelToolResponse> RunAsync(
        ModelRequest request,
        ToolExecutionContext context,
        CancellationToken cancellationToken = default)
    {
        var tools = toolRegistry.GetAvailableTools(context)
            .OrderBy(tool => tool.Name, StringComparer.Ordinal)
            .Select(tool => new ModelToolDefinition(tool.Name, tool.Description, tool.ParametersSchema))
            .ToList();
        List<JsonElement>? inputItems = null;
        var responses = new List<ModelToolResponse>();
        var executions = new List<object>();
        var stopwatch = Stopwatch.StartNew();
        var argumentFailures = 0;

        for (var iteration = 1; iteration <= DefaultMaxIterations + 1; iteration++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var response = await modelClient.RespondWithToolsAsync(
                new ModelToolRequest(request, iteration <= DefaultMaxIterations ? tools : [],
                    DefaultMaxIterations, InputItems: inputItems), cancellationToken);
            responses.Add(response);

            if (response.ToolCalls.Count == 0 || iteration > DefaultMaxIterations)
            {
                stopwatch.Stop();
                RecordTurn(context.ConversationId, responses.Count, executions.Count, stopwatch.ElapsedMilliseconds);
                return response with
                {
                    Content = response.ToolCalls.Count == 0 && !string.IsNullOrWhiteSpace(response.Content)
                        ? response.Content.Trim()
                        : "Não consegui concluir a operação porque ela excedeu o limite de etapas. Tente novamente com um pedido mais específico.",
                    AuditData = CombineAuditData(response, responses, executions, stopwatch.ElapsedMilliseconds)
                };
            }

            inputItems ??= request.InputItems?.ToList() ?? [CreateUserInputItem(request.Input)];
            inputItems.AddRange(response.OutputItems);
            foreach (var call in response.ToolCalls)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var tool = toolRegistry.Find(call.Name);
                var result = tool is null
                    ? new AegisToolResult(false, "{\"error\":\"unknown_tool\"}", "unknown_tool")
                    : await ExecuteToolSafelyAsync(tool, call, context, cancellationToken);
                executions.Add(new
                {
                    iteration,
                    callId = call.Id,
                    tool = call.Name,
                    arguments = call.Arguments,
                    result.Success,
                    result.ErrorCode,
                    result.Content,
                    result.AuditMetadataJson
                });
                inputItems.Add(CreateFunctionCallOutputItem(call.Id, result.Content));
                if (IsRecoverableArgumentFailure(result) && ++argumentFailures > 1)
                {
                    tools.Clear();
                }
            }
        }

        throw new InvalidOperationException("The bounded tool loop exited unexpectedly.");
    }

    public async IAsyncEnumerable<ModelStreamChunk> StreamAsync(
        ModelRequest request,
        ToolExecutionContext context,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var tools = toolRegistry.GetAvailableTools(context)
            .OrderBy(tool => tool.Name, StringComparer.Ordinal)
            .Select(tool => new ModelToolDefinition(tool.Name, tool.Description, tool.ParametersSchema))
            .ToList();
        var inputItems = request.InputItems?.ToList() ?? [CreateUserInputItem(request.Input)];
        var responses = new List<ModelToolStreamChunk>();
        var executions = new List<object>();
        var stopwatch = Stopwatch.StartNew();
        var argumentFailures = 0;

        for (var iteration = 1; iteration <= DefaultMaxIterations + 1; iteration++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ModelToolStreamChunk? completed = null;
            await foreach (var chunk in modelClient.RespondWithToolsStreamAsync(
                new ModelToolRequest(request, iteration <= DefaultMaxIterations ? tools : [],
                    DefaultMaxIterations, InputItems: inputItems), cancellationToken))
            {
                if (chunk.IsDone)
                {
                    completed = chunk;
                    break;
                }
                if (!string.IsNullOrEmpty(chunk.Content))
                {
                    yield return new ModelStreamChunk(chunk.Content, IsDone: false);
                }
            }

            if (completed is null || completed.AuditData is null)
            {
                throw new InvalidOperationException("Tool streaming ended without a complete response.");
            }
            responses.Add(completed);
            inputItems.AddRange(completed.OutputItems);

            if (completed.ToolCalls.Count == 0)
            {
                stopwatch.Stop();
                RecordTurn(context.ConversationId, responses.Count, executions.Count, stopwatch.ElapsedMilliseconds);
                yield return new ModelStreamChunk(null, true, completed.Provider, completed.Model,
                    completed.Purpose, completed.MetadataJson,
                    CombineStreamAuditData(completed, responses, executions, stopwatch.ElapsedMilliseconds));
                yield break;
            }

            if (iteration > DefaultMaxIterations)
            {
                yield return new ModelStreamChunk("Não consegui concluir a operação porque ela excedeu o limite de etapas. Tente novamente com um pedido mais específico.", false);
                stopwatch.Stop();
                RecordTurn(context.ConversationId, responses.Count, executions.Count, stopwatch.ElapsedMilliseconds);
                yield return new ModelStreamChunk(null, true, completed.Provider, completed.Model,
                    completed.Purpose, completed.MetadataJson,
                    CombineStreamAuditData(completed, responses, executions, stopwatch.ElapsedMilliseconds));
                yield break;
            }

            foreach (var call in completed.ToolCalls)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var (category, label, completedLabel) = ToolDisplay(call.Name);
                yield return new ModelStreamChunk(null, false, ToolStatus: new ToolStatus(category, "started", label));
                var tool = toolRegistry.Find(call.Name);
                var result = tool is null
                    ? new AegisToolResult(false, "{\"error\":\"unknown_tool\"}", "unknown_tool")
                    : await ExecuteToolSafelyAsync(tool, call, context, cancellationToken);
                executions.Add(new { iteration, tool = call.Name, result.Success, result.ErrorCode, result.AuditMetadataJson });
                inputItems.Add(CreateFunctionCallOutputItem(call.Id, result.Content));
                yield return new ModelStreamChunk(null, false, ToolStatus: new ToolStatus(
                    category, result.Success ? "completed" : "failed",
                    result.Success ? completedLabel : "Não foi possível concluir a operação"));

                if (IsRecoverableArgumentFailure(result) && ++argumentFailures > 1)
                {
                    tools.Clear();
                }
            }
        }
    }

    private static (string Category, string Started, string Completed) ToolDisplay(string name) => name switch
    {
        "email_get_status" => ("gmail", "Verificando conexão Gmail…", "Conexão verificada"),
        "email_create_connect_link" => ("gmail", "Preparando conexão Gmail…", "Link de conexão pronto"),
        "email_search" => ("gmail", "Buscando e-mails…", "Busca concluída"),
        "email_read" or "email_read_thread" => ("gmail", "Lendo e-mail…", "E-mail lido"),
        "email_confirm_pending_action" => ("gmail", "Verificando alteração…", "Alteração concluída"),
        _ => ("gmail", "Verificando alteração…", "Etapa concluída")
    };

    private void RecordTurn(Guid conversationId, int modelCalls, int toolCalls, long elapsedMilliseconds)
    {
        metrics.LlmToolCalls.Add(toolCalls);
        metrics.LlmToolIterations.Record(Math.Max(0, modelCalls - 1));
        metrics.LlmTurnModelCalls.Record(modelCalls);
        metrics.LlmTurnToolCalls.Record(toolCalls);
        metrics.LlmTurnSeconds.Record(elapsedMilliseconds / 1000.0);
        logger.LogInformation("Chat turn {ConversationId}: {ModelCalls} model calls, {ToolCalls} tool calls, {ToolIterations} tool rounds, {DurationMilliseconds} ms.",
            conversationId, modelCalls, toolCalls, Math.Max(0, modelCalls - 1), elapsedMilliseconds);
    }

    private static LlmRequestAuditData CombineStreamAuditData(
        ModelToolStreamChunk finalResponse,
        IReadOnlyList<ModelToolStreamChunk> responses,
        IReadOnlyList<object> executions,
        long durationMilliseconds)
    {
        return finalResponse.AuditData! with
        {
            DurationMilliseconds = durationMilliseconds,
            RequestPayloadJson = JsonSerializer.Serialize(new
            {
                type = "tool_loop",
                modelCalls = responses.Count,
                iterations = responses.Count,
                toolCalls = executions.Count,
                requests = responses.Select(response => response.AuditData?.RequestPayloadJson),
                toolExecutions = executions
            }, JsonOptions),
            ResponseBody = JsonSerializer.Serialize(new
            {
                type = "tool_loop",
                responses = responses.Select(response => response.AuditData?.ResponseBody)
            }, JsonOptions)
        };
    }

    private static bool IsRecoverableArgumentFailure(AegisToolResult result)
    {
        return !result.Success &&
            string.Equals(result.ErrorCode, "invalid_tool_arguments", StringComparison.Ordinal);
    }

    private async Task<AegisToolResult> ExecuteToolSafelyAsync(
        IAegisTool tool,
        ModelToolCall toolCall,
        ToolExecutionContext context,
        CancellationToken cancellationToken)
    {
        try
        {
            return await tool.ExecuteAsync(toolCall.Arguments, context, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Tool {ToolName} failed.", toolCall.Name);
            return new AegisToolResult(
                false,
                JsonSerializer.Serialize(new
                {
                    error = "tool_execution_failed",
                    message = "A ferramenta falhou temporariamente."
                }, JsonOptions),
                "tool_execution_failed");
        }
    }

    private static LlmRequestAuditData CombineAuditData(
        ModelToolResponse finalResponse,
        IReadOnlyList<ModelToolResponse> responses,
        IReadOnlyList<object> executions,
        long durationMilliseconds)
    {
        var requestPayloads = responses
            .Select((response, index) => new
            {
                iteration = index + 1,
                response.AuditData.RequestPayloadJson
            })
            .ToList();
        var responseBodies = responses
            .Select((response, index) => new
            {
                iteration = index + 1,
                response.AuditData.HttpStatusCode,
                response.AuditData.ResponseBody
            })
            .ToList();

        return finalResponse.AuditData with
        {
            DurationMilliseconds = durationMilliseconds,
            RequestPayloadJson = JsonSerializer.Serialize(new
            {
                type = "tool_loop",
                requestPayloads,
                toolExecutions = executions
            }, JsonOptions),
            ResponseBody = JsonSerializer.Serialize(new
            {
                type = "tool_loop",
                responses = responseBodies
            }, JsonOptions)
        };
    }

    private static JsonElement CreateUserInputItem(string content)
    {
        return ToJsonElement(new
        {
            role = "user",
            content
        });
    }

    private static JsonElement CreateFunctionCallOutputItem(string callId, string output)
    {
        return ToJsonElement(new
        {
            type = "function_call_output",
            call_id = callId,
            output
        });
    }

    private static JsonElement ToJsonElement(object value)
    {
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(value, JsonOptions));
        return document.RootElement.Clone();
    }
}
