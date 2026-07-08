using Aegis.Application.Models;
using Aegis.Application.Llm;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Aegis.Application.Tools;

public sealed class AegisToolLoop(
    IAegisModelClient modelClient,
    IAegisToolRegistry toolRegistry) : IAegisToolLoop
{
    private const int DefaultMaxIterations = 10;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public async Task<ModelToolResponse> RunAsync(
        ModelRequest request,
        ToolExecutionContext context,
        CancellationToken cancellationToken = default)
    {
        var tools = toolRegistry
            .GetAvailableTools(context)
            .Select(tool => new ModelToolDefinition(tool.Name, tool.Description, tool.ParametersSchema))
            .ToList();

        var maxIterations = DefaultMaxIterations;
        List<JsonElement>? inputItems = null;
        var responses = new List<ModelToolResponse>();
        var executions = new List<object>();
        var recoverableArgumentFailures = 0;
        var stopwatch = Stopwatch.StartNew();

        for (var iteration = 1; ; iteration++)
        {
            var response = await modelClient.RespondWithToolsAsync(
                new ModelToolRequest(request, tools, maxIterations, InputItems: inputItems),
                cancellationToken);
            responses.Add(response);

            if (response.ToolCalls.Count == 0)
            {
                stopwatch.Stop();
                return response with
                {
                    Content = response.Content.Trim(),
                    AuditData = CombineAuditData(response, responses, executions, stopwatch.ElapsedMilliseconds)
                };
            }

            if (iteration > maxIterations)
            {
                inputItems ??= [CreateUserInputItem(request.Input)];
                inputItems.AddRange(response.OutputItems);
                foreach (var toolCall in response.ToolCalls)
                {
                    inputItems.Add(CreateFunctionCallOutputItem(
                        toolCall.Id,
                        JsonSerializer.Serialize(new
                        {
                            error = "tool_iteration_limit_exceeded",
                            message = $"The tool loop limit of {maxIterations} was reached before this tool call could be executed. Tell Pedro the operation could not continue because it exceeded the tool step limit."
                        }, JsonOptions)));
                }

                var limitResponse = await modelClient.RespondWithToolsAsync(
                    new ModelToolRequest(request, [], maxIterations, InputItems: inputItems),
                    cancellationToken);
                responses.Add(limitResponse);

                stopwatch.Stop();
                return limitResponse with
                {
                    Content = limitResponse.Content.Trim(),
                    AuditData = CombineAuditData(limitResponse, responses, executions, stopwatch.ElapsedMilliseconds)
                };
            }

            inputItems ??= [CreateUserInputItem(request.Input)];
            inputItems.AddRange(response.OutputItems);
            foreach (var toolCall in response.ToolCalls)
            {
                var tool = toolRegistry.Find(toolCall.Name);
                AegisToolResult result;
                if (tool is null)
                {
                    result = new AegisToolResult(
                        false,
                        JsonSerializer.Serialize(new
                        {
                            error = "unknown_tool",
                            message = $"Tool '{toolCall.Name}' is not registered."
                        }, JsonOptions),
                        "unknown_tool");
                }
                else
                {
                    result = await ExecuteToolSafelyAsync(tool, toolCall, context, cancellationToken);
                }

                executions.Add(new
                {
                    iteration,
                    callId = toolCall.Id,
                    tool = toolCall.Name,
                    arguments = toolCall.Arguments,
                    result.Success,
                    result.ErrorCode,
                    result.Content,
                    result.AuditMetadataJson
                });

                if (IsRecoverableArgumentFailure(result))
                {
                    recoverableArgumentFailures++;
                    if (recoverableArgumentFailures > 1)
                    {
                        inputItems.Add(CreateFunctionCallOutputItem(toolCall.Id, result.Content));
                        var argumentFailureResponse = await modelClient.RespondWithToolsAsync(
                            new ModelToolRequest(request, [], maxIterations, InputItems: inputItems),
                            cancellationToken);
                        responses.Add(argumentFailureResponse);

                        stopwatch.Stop();
                        return argumentFailureResponse with
                        {
                            Content = argumentFailureResponse.Content.Trim(),
                            AuditData = CombineAuditData(argumentFailureResponse, responses, executions, stopwatch.ElapsedMilliseconds)
                        };
                    }
                }

                inputItems.Add(CreateFunctionCallOutputItem(toolCall.Id, result.Content));
            }
        }
    }

    private static bool IsRecoverableArgumentFailure(AegisToolResult result)
    {
        return !result.Success &&
            string.Equals(result.ErrorCode, "invalid_tool_arguments", StringComparison.Ordinal);
    }

    private static async Task<AegisToolResult> ExecuteToolSafelyAsync(
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
            return new AegisToolResult(
                false,
                JsonSerializer.Serialize(new
                {
                    error = "tool_execution_failed",
                    message = exception.Message
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
