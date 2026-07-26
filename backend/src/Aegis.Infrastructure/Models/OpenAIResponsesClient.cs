using Aegis.Application.Llm;
using Aegis.Application.Models;
using Microsoft.Extensions.Options;
using System.Diagnostics;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Aegis.Infrastructure.Models;

public sealed class OpenAIResponsesClient(
    HttpClient httpClient,
    IOptions<OpenAIOptions> options) : IAegisModelClient
{
    private const string Provider = "openai";
    private const string FriendlyFailureMessage = "Tive um problema para responder agora. Tenta de novo em alguns segundos.";
    private const string FinalAnswerPhase = "final_answer";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public async Task<ModelCompletionResponse> GenerateAsync(
        ModelRequest request,
        CancellationToken cancellationToken = default)
    {
        var model = ChooseModel(request.Purpose);
        var payload = CreateRequestPayload(request, model, stream: false, tools: null);
        var requestPayloadJson = JsonSerializer.Serialize(payload, JsonOptions);
        var stopwatch = Stopwatch.StartNew();
        int? httpStatusCode = null;
        string? responseBody = null;

        try
        {
            using var requestMessage = CreateHttpRequest(requestPayloadJson);
            using var response = await httpClient.SendAsync(requestMessage, cancellationToken);
            httpStatusCode = (int)response.StatusCode;
            responseBody = await response.Content.ReadAsStringAsync(cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                throw new InvalidOperationException($"Model request failed with HTTP {httpStatusCode}.");
            }

            using var document = JsonDocument.Parse(responseBody);
            var content = ExtractOutputText(document.RootElement);
            if (string.IsNullOrWhiteSpace(content))
            {
                throw new InvalidOperationException("Model returned an empty response.");
            }

            stopwatch.Stop();
            var responseModel = ExtractResponseModel(document.RootElement, model);
            var metadataJson = BuildMetadataJson(
                request.Purpose,
                request.Metadata,
                document.RootElement);

            return new ModelCompletionResponse(
                content.Trim(),
                Provider,
                responseModel,
                request.Purpose,
                metadataJson,
                new LlmRequestAuditData(
                    Provider,
                    responseModel,
                    Success: true,
                    stopwatch.ElapsedMilliseconds,
                    requestPayloadJson,
                    httpStatusCode,
                    responseBody,
                    FailureReason: null,
                    ErrorType: null));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is not LlmRequestException)
        {
            stopwatch.Stop();
            throw CreateRequestException(
                exception,
                model,
                request.Purpose,
                requestPayloadJson,
                httpStatusCode,
                responseBody,
                stopwatch.ElapsedMilliseconds);
        }
    }

    public async IAsyncEnumerable<ModelStreamChunk> StreamAsync(
        ModelRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var model = ChooseModel(request.Purpose);
        var payload = CreateRequestPayload(request, model, stream: true, tools: null);
        var requestPayloadJson = JsonSerializer.Serialize(payload, JsonOptions);
        var responseBody = new StringBuilder();
        var completedResponseBody = new StringBuilder();
        var stopwatch = Stopwatch.StartNew();
        int? httpStatusCode = null;
        HttpResponseMessage? response = null;
        JsonElement? completedResponse = null;
        string responseModel = model;
        var outputPhasesByIndex = new Dictionary<int, string?>();
        var outputPhasesByItemId = new Dictionary<string, string?>(StringComparer.Ordinal);

        try
        {
            using var requestMessage = CreateHttpRequest(requestPayloadJson);
            response = await httpClient.SendAsync(
                requestMessage,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
            httpStatusCode = (int)response.StatusCode;

            if (!response.IsSuccessStatusCode)
            {
                responseBody.Append(await response.Content.ReadAsStringAsync(cancellationToken));
                throw new InvalidOperationException($"Model request failed with HTTP {httpStatusCode}.");
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            response?.Dispose();
            throw;
        }
        catch (Exception exception) when (exception is not LlmRequestException &&
            !(exception is OperationCanceledException && cancellationToken.IsCancellationRequested))
        {
            response?.Dispose();
            throw CreateRequestException(
                exception,
                model,
                request.Purpose,
                requestPayloadJson,
                httpStatusCode,
                responseBody.ToString().TrimEnd(),
                stopwatch.ElapsedMilliseconds);
        }

        using (response)
        {
            Stream responseStream;
            try
            {
                responseStream = await response.Content.ReadAsStreamAsync(cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                throw CreateRequestException(
                    exception,
                    model,
                    request.Purpose,
                    requestPayloadJson,
                    httpStatusCode,
                    responseBody.ToString().TrimEnd(),
                    stopwatch.ElapsedMilliseconds);
            }

            await using (responseStream)
            using (var reader = new StreamReader(responseStream))
            {
                while (true)
                {
                    string? line;
                    try
                    {
                        line = await reader.ReadLineAsync(cancellationToken);
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception exception)
                    {
                        throw CreateRequestException(
                            exception,
                            model,
                            request.Purpose,
                            requestPayloadJson,
                            httpStatusCode,
                            responseBody.ToString().TrimEnd(),
                            stopwatch.ElapsedMilliseconds);
                    }

                    if (line is null)
                    {
                        break;
                    }

                    if (string.IsNullOrWhiteSpace(line) || !line.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    var data = line["data:".Length..].Trim();
                    if (string.Equals(data, "[DONE]", StringComparison.Ordinal))
                    {
                        break;
                    }

                    responseBody.AppendLine(data);
                    JsonDocument document;
                    try
                    {
                        document = JsonDocument.Parse(data);
                    }
                    catch (JsonException exception)
                    {
                        throw CreateRequestException(
                            exception,
                            model,
                            request.Purpose,
                            requestPayloadJson,
                            httpStatusCode,
                            responseBody.ToString().TrimEnd(),
                            stopwatch.ElapsedMilliseconds);
                    }

                    using (document)
                    {
                        var root = document.RootElement;
                        var eventType = root.TryGetProperty("type", out var typeElement)
                            ? typeElement.GetString()
                            : null;

                        TrackOutputItemPhase(root, outputPhasesByIndex, outputPhasesByItemId);

                        if (string.Equals(eventType, "response.output_text.delta", StringComparison.Ordinal) &&
                            root.TryGetProperty("delta", out var deltaElement))
                        {
                            var delta = deltaElement.GetString();
                            if (!string.IsNullOrEmpty(delta) &&
                                IsFinalAnswerTextDelta(root, outputPhasesByIndex, outputPhasesByItemId))
                            {
                                yield return new ModelStreamChunk(delta, IsDone: false);
                            }

                            continue;
                        }

                        if (string.Equals(eventType, "response.completed", StringComparison.Ordinal) &&
                            root.TryGetProperty("response", out var responseElement))
                        {
                            completedResponse = responseElement.Clone();
                            responseModel = ExtractResponseModel(responseElement, model);
                            completedResponseBody.Append(responseElement.GetRawText());
                            continue;
                        }

                        if (string.Equals(eventType, "response.failed", StringComparison.Ordinal) ||
                            string.Equals(eventType, "error", StringComparison.Ordinal))
                        {
                            throw CreateRequestException(
                                new InvalidOperationException("Model stream failed."),
                                model,
                                request.Purpose,
                                requestPayloadJson,
                                httpStatusCode,
                                responseBody.ToString().TrimEnd(),
                                stopwatch.ElapsedMilliseconds);
                        }
                    }
                }
            }
        }

        if (completedResponse is null)
        {
            throw CreateRequestException(
                new InvalidOperationException("Model stream ended without a completion event."),
                model,
                request.Purpose,
                requestPayloadJson,
                httpStatusCode,
                responseBody.ToString().TrimEnd(),
                stopwatch.ElapsedMilliseconds);
        }

        stopwatch.Stop();
        var metadataJson = BuildMetadataJson(
            request.Purpose,
            request.Metadata,
            completedResponse.Value);
        var finalResponseBody = completedResponseBody.Length > 0
            ? completedResponseBody.ToString()
            : responseBody.ToString().TrimEnd();

        yield return new ModelStreamChunk(
            Content: null,
            IsDone: true,
            Provider,
            responseModel,
            request.Purpose,
            metadataJson,
            new LlmRequestAuditData(
                Provider,
                responseModel,
                Success: true,
                stopwatch.ElapsedMilliseconds,
                requestPayloadJson,
                httpStatusCode,
                finalResponseBody,
                FailureReason: null,
                ErrorType: null));
    }

    public async Task<ModelToolResponse> RespondWithToolsAsync(
        ModelToolRequest request,
        CancellationToken cancellationToken = default)
    {
        var model = ChooseModel(ModelPurpose.Main);
        var payload = CreateRequestPayload(
            request.Request with { Purpose = ModelPurpose.Main },
            model,
            stream: false,
            request.Tools,
            request.PreviousResponseId,
            request.ToolOutputs,
            request.InputItems);
        var requestPayloadJson = JsonSerializer.Serialize(payload, JsonOptions);
        var stopwatch = Stopwatch.StartNew();
        int? httpStatusCode = null;
        string? responseBody = null;

        try
        {
            using var requestMessage = CreateHttpRequest(requestPayloadJson);
            using var response = await httpClient.SendAsync(requestMessage, cancellationToken);
            httpStatusCode = (int)response.StatusCode;
            responseBody = await response.Content.ReadAsStringAsync(cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                throw new InvalidOperationException($"Model tool request failed with HTTP {httpStatusCode}.");
            }

            using var document = JsonDocument.Parse(responseBody);
            var content = ExtractOutputText(document.RootElement);
            var toolCalls = ExtractToolCalls(document.RootElement);
            stopwatch.Stop();
            var responseModel = ExtractResponseModel(document.RootElement, model);

            return new ModelToolResponse(
                content.Trim(),
                Provider,
                responseModel,
                ModelPurpose.Main,
                toolCalls,
                ExtractOutputItems(document.RootElement),
                ExtractResponseId(document.RootElement),
                BuildMetadataJson(ModelPurpose.Main, request.Request.Metadata, document.RootElement),
                new LlmRequestAuditData(
                    Provider,
                    responseModel,
                    Success: true,
                    stopwatch.ElapsedMilliseconds,
                    requestPayloadJson,
                    httpStatusCode,
                    responseBody,
                    FailureReason: null,
                    ErrorType: null));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is not LlmRequestException)
        {
            stopwatch.Stop();
            throw CreateRequestException(
                exception,
                model,
                ModelPurpose.Main,
                requestPayloadJson,
                httpStatusCode,
                responseBody,
                stopwatch.ElapsedMilliseconds);
        }
    }

    public async IAsyncEnumerable<ModelToolStreamChunk> RespondWithToolsStreamAsync(
        ModelToolRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var model = ChooseModel(ModelPurpose.Main);
        var payload = CreateRequestPayload(
            request.Request with { Purpose = ModelPurpose.Main },
            model,
            stream: true,
            request.Tools,
            request.PreviousResponseId,
            request.ToolOutputs,
            request.InputItems);
        var requestPayloadJson = JsonSerializer.Serialize(payload, JsonOptions);
        var responseBody = new StringBuilder();
        var completedResponseBody = new StringBuilder();
        var bufferedContent = new StringBuilder();
        var stopwatch = Stopwatch.StartNew();
        int? httpStatusCode = null;
        HttpResponseMessage? response = null;
        JsonElement? completedResponse = null;
        string responseModel = model;
        var suppressDeltas = request.Tools.Count > 0;
        var outputPhasesByIndex = new Dictionary<int, string?>();
        var outputPhasesByItemId = new Dictionary<string, string?>(StringComparer.Ordinal);

        try
        {
            using var requestMessage = CreateHttpRequest(requestPayloadJson);
            response = await httpClient.SendAsync(
                requestMessage,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
            httpStatusCode = (int)response.StatusCode;

            if (!response.IsSuccessStatusCode)
            {
                responseBody.Append(await response.Content.ReadAsStringAsync(cancellationToken));
                throw new InvalidOperationException($"Model tool stream request failed with HTTP {httpStatusCode}.");
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            response?.Dispose();
            throw;
        }
        catch (Exception exception) when (exception is not LlmRequestException &&
            !(exception is OperationCanceledException && cancellationToken.IsCancellationRequested))
        {
            response?.Dispose();
            throw CreateRequestException(
                exception,
                model,
                ModelPurpose.Main,
                requestPayloadJson,
                httpStatusCode,
                responseBody.ToString().TrimEnd(),
                stopwatch.ElapsedMilliseconds);
        }

        using (response)
        {
            Stream responseStream;
            try
            {
                responseStream = await response.Content.ReadAsStreamAsync(cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                throw CreateRequestException(
                    exception,
                    model,
                    ModelPurpose.Main,
                    requestPayloadJson,
                    httpStatusCode,
                    responseBody.ToString().TrimEnd(),
                    stopwatch.ElapsedMilliseconds);
            }

            await using (responseStream)
            using (var reader = new StreamReader(responseStream))
            {
                while (true)
                {
                    string? line;
                    try
                    {
                        line = await reader.ReadLineAsync(cancellationToken);
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception exception)
                    {
                        throw CreateRequestException(
                            exception,
                            model,
                            ModelPurpose.Main,
                            requestPayloadJson,
                            httpStatusCode,
                            responseBody.ToString().TrimEnd(),
                            stopwatch.ElapsedMilliseconds);
                    }

                    if (line is null)
                    {
                        break;
                    }

                    if (string.IsNullOrWhiteSpace(line) ||
                        !line.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    var data = line["data:".Length..].Trim();
                    if (string.Equals(data, "[DONE]", StringComparison.Ordinal))
                    {
                        break;
                    }

                    responseBody.AppendLine(data);
                    JsonDocument document;
                    try
                    {
                        document = JsonDocument.Parse(data);
                    }
                    catch (JsonException exception)
                    {
                        throw CreateRequestException(
                            exception,
                            model,
                            ModelPurpose.Main,
                            requestPayloadJson,
                            httpStatusCode,
                            responseBody.ToString().TrimEnd(),
                            stopwatch.ElapsedMilliseconds);
                    }

                    using (document)
                    {
                        var root = document.RootElement;
                        var eventType = root.TryGetProperty("type", out var typeElement)
                            ? typeElement.GetString()
                            : null;

                        TrackOutputItemPhase(root, outputPhasesByIndex, outputPhasesByItemId);

                        if (string.Equals(eventType, "response.output_text.delta", StringComparison.Ordinal) &&
                            root.TryGetProperty("delta", out var deltaElement))
                        {
                            var delta = deltaElement.GetString();
                            if (!string.IsNullOrEmpty(delta) &&
                                IsFinalAnswerTextDelta(root, outputPhasesByIndex, outputPhasesByItemId))
                            {
                                if (suppressDeltas)
                                {
                                    bufferedContent.Append(delta);
                                }
                                else
                                {
                                    yield return new ModelToolStreamChunk(
                                        delta,
                                        IsDone: false,
                                        ToolCalls: [],
                                        OutputItems: []);
                                }
                            }

                            continue;
                        }

                        if (string.Equals(eventType, "response.completed", StringComparison.Ordinal) &&
                            root.TryGetProperty("response", out var responseElement))
                        {
                            completedResponse = responseElement.Clone();
                            responseModel = ExtractResponseModel(responseElement, model);
                            completedResponseBody.Append(responseElement.GetRawText());
                            continue;
                        }

                        if (string.Equals(eventType, "response.failed", StringComparison.Ordinal) ||
                            string.Equals(eventType, "error", StringComparison.Ordinal))
                        {
                            throw CreateRequestException(
                                new InvalidOperationException("Model tool stream failed."),
                                model,
                                ModelPurpose.Main,
                                requestPayloadJson,
                                httpStatusCode,
                                responseBody.ToString().TrimEnd(),
                                stopwatch.ElapsedMilliseconds);
                        }
                    }
                }
            }
        }

        if (completedResponse is null)
        {
            throw CreateRequestException(
                new InvalidOperationException("Model tool stream ended without a completion event."),
                model,
                ModelPurpose.Main,
                requestPayloadJson,
                httpStatusCode,
                responseBody.ToString().TrimEnd(),
                stopwatch.ElapsedMilliseconds);
        }

        var toolCalls = ExtractToolCalls(completedResponse.Value);
        if (suppressDeltas && toolCalls.Count == 0 && bufferedContent.Length > 0)
        {
            yield return new ModelToolStreamChunk(
                bufferedContent.ToString(),
                IsDone: false,
                ToolCalls: [],
                OutputItems: []);
        }

        stopwatch.Stop();
        var metadataJson = BuildMetadataJson(
            ModelPurpose.Main,
            request.Request.Metadata,
            completedResponse.Value);
        var finalResponseBody = completedResponseBody.Length > 0
            ? completedResponseBody.ToString()
            : responseBody.ToString().TrimEnd();

        yield return new ModelToolStreamChunk(
            Content: null,
            IsDone: true,
            toolCalls,
            ExtractOutputItems(completedResponse.Value),
            ExtractResponseId(completedResponse.Value),
            Provider,
            responseModel,
            ModelPurpose.Main,
            metadataJson,
            new LlmRequestAuditData(
                Provider,
                responseModel,
                Success: true,
                stopwatch.ElapsedMilliseconds,
                requestPayloadJson,
                httpStatusCode,
                finalResponseBody,
                FailureReason: null,
                ErrorType: null));
    }

    private HttpRequestMessage CreateHttpRequest(string requestPayloadJson)
    {
        var apiKey = options.Value.ApiKey;
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new InvalidOperationException("OPENAI_API_KEY is not configured.");
        }

        var request = new HttpRequestMessage(HttpMethod.Post, "v1/responses")
        {
            Content = new StringContent(requestPayloadJson, Encoding.UTF8, "application/json")
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

        return request;
    }

    private object CreateRequestPayload(
        ModelRequest request,
        string model,
        bool stream,
        IReadOnlyList<ModelToolDefinition>? tools,
        string? previousResponseId = null,
        IReadOnlyList<ModelToolOutput>? toolOutputs = null,
        IReadOnlyList<JsonElement>? inputItems = null)
    {
        var openAIOptions = options.Value;
        var metadata = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["aegis_version"] = "0.3.0",
            ["purpose"] = request.Purpose.ToString()
        };

        if (request.Metadata is not null)
        {
            foreach (var item in request.Metadata)
            {
                metadata[item.Key] = item.Value;
            }
        }

        object input = inputItems is { Count: > 0 }
            ? inputItems
            : toolOutputs is { Count: > 0 }
            ? toolOutputs.Select(output => new
            {
                type = "function_call_output",
                call_id = output.CallId,
                output = output.Output
            }).ToList()
            : request.Input;

        return new
        {
            model,
            instructions = request.Instructions,
            input,
            stream,
            max_output_tokens = Math.Max(1, openAIOptions.MaxOutputTokens),
            store = openAIOptions.StoreResponses,
            previous_response_id = string.IsNullOrWhiteSpace(previousResponseId) ? null : previousResponseId,
            service_tier = string.IsNullOrWhiteSpace(openAIOptions.ServiceTier)
                ? null
                : openAIOptions.ServiceTier,
            metadata,
            tools = tools?.Select(tool => new
            {
                type = "function",
                name = tool.Name,
                description = tool.Description,
                parameters = tool.ParametersSchema,
                strict = false
            }),
            parallel_tool_calls = tools is { Count: > 0 } ? true : (bool?)null,
            tool_choice = tools is { Count: > 0 } ? "auto" : null
        };
    }

    private string ChooseModel(ModelPurpose purpose)
    {
        var openAIOptions = options.Value;
        return purpose switch
        {
            ModelPurpose.Main => Normalize(openAIOptions.MainModel, OpenAIOptions.DefaultMainModel),
            ModelPurpose.Escalation => Normalize(openAIOptions.EscalationModel, OpenAIOptions.DefaultEscalationModel),
            _ => Normalize(openAIOptions.DefaultModel, OpenAIOptions.DefaultDefaultModel)
        };
    }

    private static string Normalize(string? value, string fallback)
    {
        return string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
    }

    private static string ExtractOutputText(JsonElement root)
    {
        if (root.TryGetProperty("output_text", out var outputTextElement) &&
            outputTextElement.ValueKind == JsonValueKind.String)
        {
            return outputTextElement.GetString() ?? string.Empty;
        }

        if (!root.TryGetProperty("output", out var outputElement) ||
            outputElement.ValueKind != JsonValueKind.Array)
        {
            return string.Empty;
        }

        var builder = new StringBuilder();
        foreach (var outputItem in outputElement.EnumerateArray())
        {
            if (!IsFinalAnswerOutputItem(outputItem))
            {
                continue;
            }

            if (!outputItem.TryGetProperty("content", out var contentElement) ||
                contentElement.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var contentItem in contentElement.EnumerateArray())
            {
                if (contentItem.TryGetProperty("text", out var textElement) &&
                    textElement.ValueKind == JsonValueKind.String)
                {
                    builder.Append(textElement.GetString());
                }
            }
        }

        return builder.ToString();
    }

    private static void TrackOutputItemPhase(
        JsonElement root,
        IDictionary<int, string?> outputPhasesByIndex,
        IDictionary<string, string?> outputPhasesByItemId)
    {
        if (!root.TryGetProperty("item", out var itemElement) ||
            itemElement.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        if (!TryGetPhase(itemElement, out var phase))
        {
            return;
        }

        if (root.TryGetProperty("output_index", out var outputIndexElement) &&
            outputIndexElement.ValueKind == JsonValueKind.Number &&
            outputIndexElement.TryGetInt32(out var outputIndex))
        {
            outputPhasesByIndex[outputIndex] = phase;
        }

        if (itemElement.TryGetProperty("id", out var idElement) &&
            idElement.ValueKind == JsonValueKind.String &&
            !string.IsNullOrWhiteSpace(idElement.GetString()))
        {
            outputPhasesByItemId[idElement.GetString()!] = phase;
        }
    }

    private static bool IsFinalAnswerTextDelta(
        JsonElement root,
        IReadOnlyDictionary<int, string?> outputPhasesByIndex,
        IReadOnlyDictionary<string, string?> outputPhasesByItemId)
    {
        if (TryGetPhase(root, out var phase))
        {
            return IsFinalAnswerPhase(phase);
        }

        if (root.TryGetProperty("output_index", out var outputIndexElement) &&
            outputIndexElement.ValueKind == JsonValueKind.Number &&
            outputIndexElement.TryGetInt32(out var outputIndex) &&
            outputPhasesByIndex.TryGetValue(outputIndex, out phase))
        {
            return IsFinalAnswerPhase(phase);
        }

        if (root.TryGetProperty("item_id", out var itemIdElement) &&
            itemIdElement.ValueKind == JsonValueKind.String &&
            !string.IsNullOrWhiteSpace(itemIdElement.GetString()) &&
            outputPhasesByItemId.TryGetValue(itemIdElement.GetString()!, out phase))
        {
            return IsFinalAnswerPhase(phase);
        }

        return true;
    }

    private static bool IsFinalAnswerOutputItem(JsonElement outputItem)
    {
        return !TryGetPhase(outputItem, out var phase) || IsFinalAnswerPhase(phase);
    }

    private static bool TryGetPhase(JsonElement element, out string? phase)
    {
        phase = ExtractPhase(element);
        return phase is not null;
    }

    private static string? ExtractPhase(JsonElement element)
    {
        return element.TryGetProperty("phase", out var phaseElement) &&
            phaseElement.ValueKind == JsonValueKind.String
            ? phaseElement.GetString()
            : null;
    }

    private static bool IsFinalAnswerPhase(string? phase)
    {
        return string.Equals(phase, FinalAnswerPhase, StringComparison.Ordinal);
    }

    private static IReadOnlyList<ModelToolCall> ExtractToolCalls(JsonElement root)
    {
        if (!root.TryGetProperty("output", out var outputElement) ||
            outputElement.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var calls = new List<ModelToolCall>();
        foreach (var outputItem in outputElement.EnumerateArray())
        {
            if (!outputItem.TryGetProperty("type", out var typeElement) ||
                !string.Equals(typeElement.GetString(), "function_call", StringComparison.Ordinal))
            {
                continue;
            }

            var id = outputItem.TryGetProperty("call_id", out var callIdElement)
                ? callIdElement.GetString()
                : null;
            var name = outputItem.TryGetProperty("name", out var nameElement)
                ? nameElement.GetString()
                : null;
            var argumentsText = outputItem.TryGetProperty("arguments", out var argumentsElement)
                ? argumentsElement.GetString()
                : null;

            if (string.IsNullOrWhiteSpace(id) ||
                string.IsNullOrWhiteSpace(name) ||
                string.IsNullOrWhiteSpace(argumentsText))
            {
                continue;
            }

            using var argumentsDocument = JsonDocument.Parse(argumentsText);
            calls.Add(new ModelToolCall(id, name, argumentsDocument.RootElement.Clone()));
        }

        return calls;
    }

    private static IReadOnlyList<JsonElement> ExtractOutputItems(JsonElement root)
    {
        if (!root.TryGetProperty("output", out var outputElement) ||
            outputElement.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return outputElement
            .EnumerateArray()
            .Select(item => item.Clone())
            .ToList();
    }

    private static string ExtractResponseModel(JsonElement root, string fallback)
    {
        return root.TryGetProperty("model", out var modelElement) &&
            modelElement.ValueKind == JsonValueKind.String &&
            !string.IsNullOrWhiteSpace(modelElement.GetString())
            ? modelElement.GetString()!
            : fallback;
    }

    private static string? ExtractResponseId(JsonElement root)
    {
        return root.TryGetProperty("id", out var idElement) &&
            idElement.ValueKind == JsonValueKind.String &&
            !string.IsNullOrWhiteSpace(idElement.GetString())
            ? idElement.GetString()
            : null;
    }

    private static string BuildMetadataJson(
        ModelPurpose purpose,
        IReadOnlyDictionary<string, string>? requestMetadata,
        JsonElement response)
    {
        JsonElement? usage = response.TryGetProperty("usage", out var usageElement)
            ? usageElement.Clone()
            : null;

        var metadata = new
        {
            provider = Provider,
            purpose = purpose.ToString(),
            requestMetadata,
            usage,
            responseId = response.TryGetProperty("id", out var idElement) ? idElement.GetString() : null,
            status = response.TryGetProperty("status", out var statusElement) ? statusElement.GetString() : null
        };

        return JsonSerializer.Serialize(metadata, JsonOptions);
    }

    private static LlmRequestException CreateRequestException(
        Exception exception,
        string model,
        ModelPurpose purpose,
        string requestPayloadJson,
        int? httpStatusCode,
        string? responseBody,
        long durationMilliseconds)
    {
        var auditData = new LlmRequestAuditData(
            Provider,
            model,
            Success: false,
            durationMilliseconds,
            requestPayloadJson,
            httpStatusCode,
            responseBody,
            exception.Message,
            exception.GetType().FullName);

        return new LlmRequestException(FriendlyFailureMessage, auditData, exception);
    }
}
