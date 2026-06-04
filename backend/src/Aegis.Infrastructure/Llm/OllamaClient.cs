using System.Net.Http.Json;
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

        var request = new OllamaGenerateRequest(
            model,
            prompt,
            Stream: false,
            new OllamaGenerateOptions(ollamaOptions.Temperature, ollamaOptions.NumCtx));

        using var response = await httpClient.PostAsJsonAsync("api/generate", request, JsonOptions, cancellationToken);
        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"Ollama request failed with HTTP {(int)response.StatusCode}: {responseBody}");
        }

        var ollamaResponse = JsonSerializer.Deserialize<OllamaGenerateResponse>(responseBody, JsonOptions)
            ?? throw new InvalidOperationException("Ollama returned an empty response.");

        if (string.IsNullOrWhiteSpace(ollamaResponse.Response))
        {
            throw new InvalidOperationException("Ollama returned an empty completion.");
        }

        return new LlmCompletionResponse(
            ollamaResponse.Response.Trim(),
            string.IsNullOrWhiteSpace(ollamaResponse.Model) ? model : ollamaResponse.Model,
            BuildMetadataJson(ollamaResponse));
    }

    private static string BuildMetadataJson(OllamaGenerateResponse response)
    {
        var metadata = new
        {
            provider = "ollama",
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
