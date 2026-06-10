using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Aegis.Application.Llm;
using Microsoft.Extensions.Options;

namespace Aegis.Infrastructure.Llm;

public sealed class OllamaClient(HttpClient httpClient, IOptions<OllamaOptions> options) : ILlmClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public async Task<LlmCompletionResponse> GenerateAsync(
        string prompt,
        CancellationToken cancellationToken = default)
    {
        var ollamaOptions = options.Value;
        var model = string.IsNullOrWhiteSpace(ollamaOptions.Model)
            ? OllamaOptions.DefaultModel
            : ollamaOptions.Model;
        var keepAlive = string.IsNullOrWhiteSpace(ollamaOptions.KeepAlive)
            ? OllamaOptions.DefaultKeepAlive
            : ollamaOptions.KeepAlive;

        var request = new OllamaGenerateRequest(
            model,
            prompt,
            Stream: false,
            keepAlive,
            new OllamaGenerateOptions(ollamaOptions.Temperature, ollamaOptions.NumCtx));
        var requestPayload = JsonSerializer.SerializeToElement(request, JsonOptions);
        var requestPayloadJson = requestPayload.GetRawText();
        var stopwatch = Stopwatch.StartNew();
        int? httpStatusCode = null;
        string? responseBody = null;

        try
        {
            using var requestContent = new StringContent(
                requestPayloadJson,
                Encoding.UTF8,
                "application/json");
            using var response = await httpClient.PostAsync("api/generate", requestContent, cancellationToken);
            httpStatusCode = (int)response.StatusCode;
            responseBody = await response.Content.ReadAsStringAsync(cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                throw new InvalidOperationException(
                    $"Ollama request failed with HTTP {httpStatusCode}.");
            }

            var ollamaResponse = JsonSerializer.Deserialize<OllamaGenerateResponse>(responseBody, JsonOptions)
                ?? throw new InvalidOperationException("Ollama returned an empty response.");

            if (string.IsNullOrWhiteSpace(ollamaResponse.Response))
            {
                throw new InvalidOperationException("Ollama returned an empty completion.");
            }

            stopwatch.Stop();
            var responseModel = string.IsNullOrWhiteSpace(ollamaResponse.Model) ? model : ollamaResponse.Model;

            return new LlmCompletionResponse(
                ollamaResponse.Response.Trim(),
                responseModel,
                BuildMetadataJson(requestPayload, ollamaResponse),
                new LlmRequestAuditData(
                    "ollama",
                    responseModel,
                    Success: true,
                    stopwatch.ElapsedMilliseconds,
                    requestPayloadJson,
                    httpStatusCode,
                    responseBody,
                    FailureReason: null,
                    ErrorType: null));
        }
        catch (Exception exception) when (exception is not LlmRequestException)
        {
            stopwatch.Stop();
            var auditData = new LlmRequestAuditData(
                "ollama",
                model,
                Success: false,
                stopwatch.ElapsedMilliseconds,
                requestPayloadJson,
                httpStatusCode,
                responseBody,
                exception.Message,
                exception.GetType().FullName);

            throw new LlmRequestException("Ollama could not complete the generation request.", auditData, exception);
        }
    }

    public async IAsyncEnumerable<LlmStreamChunk> StreamCompletionAsync(
        string prompt,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var ollamaOptions = options.Value;
        var model = string.IsNullOrWhiteSpace(ollamaOptions.Model)
            ? OllamaOptions.DefaultModel
            : ollamaOptions.Model;
        var keepAlive = string.IsNullOrWhiteSpace(ollamaOptions.KeepAlive)
            ? OllamaOptions.DefaultKeepAlive
            : ollamaOptions.KeepAlive;

        var request = new OllamaGenerateRequest(
            model,
            prompt,
            Stream: true,
            keepAlive,
            new OllamaGenerateOptions(ollamaOptions.Temperature, ollamaOptions.NumCtx));
        var requestPayload = JsonSerializer.SerializeToElement(request, JsonOptions);
        var requestPayloadJson = requestPayload.GetRawText();
        var responseBody = new StringBuilder();
        var stopwatch = Stopwatch.StartNew();
        int? httpStatusCode = null;
        HttpResponseMessage? response = null;

        try
        {
            using var requestMessage = new HttpRequestMessage(HttpMethod.Post, "api/generate")
            {
                Content = new StringContent(requestPayloadJson, Encoding.UTF8, "application/json")
            };
            response = await httpClient.SendAsync(
                requestMessage,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
            httpStatusCode = (int)response.StatusCode;

            if (!response.IsSuccessStatusCode)
            {
                responseBody.Append(await response.Content.ReadAsStringAsync(cancellationToken));
                throw new InvalidOperationException(
                    $"Ollama request failed with HTTP {httpStatusCode}.");
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is not LlmRequestException)
        {
            response?.Dispose();
            throw CreateStreamingException(
                exception,
                model,
                requestPayloadJson,
                httpStatusCode,
                responseBody,
                stopwatch);
        }

        using (var streamingResponse = response ??
            throw new InvalidOperationException("Ollama returned no HTTP response."))
        {
            Stream responseStream;
            try
            {
                responseStream = await streamingResponse.Content.ReadAsStreamAsync(cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                throw CreateStreamingException(
                    exception,
                    model,
                    requestPayloadJson,
                    httpStatusCode,
                    responseBody,
                    stopwatch);
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
                        throw CreateStreamingException(
                            exception,
                            model,
                            requestPayloadJson,
                            httpStatusCode,
                            responseBody,
                            stopwatch);
                    }

                    if (line is null)
                    {
                        throw CreateStreamingException(
                            new InvalidOperationException(
                                "Ollama closed the stream before sending a final response."),
                            model,
                            requestPayloadJson,
                            httpStatusCode,
                            responseBody,
                            stopwatch);
                    }

                    if (string.IsNullOrWhiteSpace(line))
                    {
                        continue;
                    }

                    responseBody.AppendLine(line);
                    OllamaGenerateResponse ollamaResponse;
                    try
                    {
                        ollamaResponse = JsonSerializer.Deserialize<OllamaGenerateResponse>(line, JsonOptions)
                            ?? throw new InvalidOperationException(
                                "Ollama returned an invalid streaming response.");
                    }
                    catch (Exception exception)
                    {
                        throw CreateStreamingException(
                            exception,
                            model,
                            requestPayloadJson,
                            httpStatusCode,
                            responseBody,
                            stopwatch);
                    }

                    if (!string.IsNullOrEmpty(ollamaResponse.Response))
                    {
                        yield return new LlmStreamChunk(ollamaResponse.Response, IsDone: false);
                    }

                    if (!ollamaResponse.Done)
                    {
                        continue;
                    }

                    stopwatch.Stop();
                    var responseModel = string.IsNullOrWhiteSpace(ollamaResponse.Model)
                        ? model
                        : ollamaResponse.Model;
                    var responseBodyText = responseBody.ToString().TrimEnd();

                    yield return new LlmStreamChunk(
                        Content: null,
                        IsDone: true,
                        responseModel,
                        BuildMetadataJson(requestPayload, ollamaResponse),
                        new LlmRequestAuditData(
                            "ollama",
                            responseModel,
                            Success: true,
                            stopwatch.ElapsedMilliseconds,
                            requestPayloadJson,
                            httpStatusCode,
                            responseBodyText,
                            FailureReason: null,
                            ErrorType: null));
                    yield break;
                }
            }
        }
    }

    private static LlmRequestException CreateStreamingException(
        Exception exception,
        string model,
        string requestPayloadJson,
        int? httpStatusCode,
        StringBuilder responseBody,
        Stopwatch stopwatch)
    {
        stopwatch.Stop();
        var auditData = new LlmRequestAuditData(
            "ollama",
            model,
            Success: false,
            stopwatch.ElapsedMilliseconds,
            requestPayloadJson,
            httpStatusCode,
            responseBody.ToString().TrimEnd(),
            exception.Message,
            exception.GetType().FullName);

        return new LlmRequestException(
            "Ollama could not complete the streaming generation request.",
            auditData,
            exception);
    }

    private static string BuildMetadataJson(JsonElement requestPayload, OllamaGenerateResponse response)
    {
        var metadata = new
        {
            provider = "ollama",
            requestPayload,
            response.CreatedAt,
            response.Done,
            response.TotalDuration,
            response.LoadDuration,
            response.PromptEvalCount,
            response.PromptEvalDuration,
            response.EvalCount,
            response.EvalDuration
        };

        return JsonSerializer.Serialize(metadata, JsonOptions);
    }

    private sealed record OllamaGenerateRequest(
        [property: JsonPropertyName("model")] string Model,
        [property: JsonPropertyName("prompt")] string Prompt,
        [property: JsonPropertyName("stream")] bool Stream,
        [property: JsonPropertyName("keep_alive")] string KeepAlive,
        [property: JsonPropertyName("options")] OllamaGenerateOptions Options);

    private sealed record OllamaGenerateOptions(
        [property: JsonPropertyName("temperature")] double? Temperature,
        [property: JsonPropertyName("num_ctx")] int? NumCtx);

    private sealed class OllamaGenerateResponse
    {
        [JsonPropertyName("model")]
        public string? Model { get; init; }

        [JsonPropertyName("created_at")]
        public DateTimeOffset? CreatedAt { get; init; }

        [JsonPropertyName("response")]
        public string? Response { get; init; }

        [JsonPropertyName("done")]
        public bool Done { get; init; }

        [JsonPropertyName("total_duration")]
        public long? TotalDuration { get; init; }

        [JsonPropertyName("load_duration")]
        public long? LoadDuration { get; init; }

        [JsonPropertyName("prompt_eval_count")]
        public int? PromptEvalCount { get; init; }

        [JsonPropertyName("prompt_eval_duration")]
        public long? PromptEvalDuration { get; init; }

        [JsonPropertyName("eval_count")]
        public int? EvalCount { get; init; }

        [JsonPropertyName("eval_duration")]
        public long? EvalDuration { get; init; }
    }
}
