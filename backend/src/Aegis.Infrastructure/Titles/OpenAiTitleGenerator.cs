using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Aegis.Application.Chat;
using Aegis.Infrastructure.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Aegis.Infrastructure.Titles;

public sealed class OpenAiTitleGenerator(
    HttpClient httpClient,
    IOptions<TitleOptions> options,
    IOptions<OpenAIOptions> openAiOptions,
    ILogger<OpenAiTitleGenerator> logger) : IConversationTitleGenerator
{
    public async Task<string?> GenerateAsync(
        string userContent,
        string assistantContent,
        CancellationToken cancellationToken = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, options.Value.TimeoutSeconds)));

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "v1/responses")
            {
                Content = JsonContent.Create(new
                {
                    model = options.Value.Model,
                    reasoning = new { effort = options.Value.ReasoningEffort },
                    input = new object[]
                    {
                        new { role = "developer", content = "Crie um título curto em português brasileiro (até 50 caracteres) para a conversa. Responda só com o título, sem aspas." },
                        new { role = "user", content = $"Mensagem: {userContent}\nResposta: {assistantContent}" }
                    },
                    max_output_tokens = Math.Clamp(options.Value.MaxOutputTokens, 16, 128),
                    store = false
                })
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", openAiOptions.Value.ApiKey);
            using var response = await httpClient.SendAsync(request, timeout.Token);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("Title generation failed with HTTP {StatusCode}.", (int)response.StatusCode);
                return null;
            }

            using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(timeout.Token), cancellationToken: timeout.Token);
            var root = document.RootElement;
            var status = root.TryGetProperty("status", out var statusValue) && statusValue.ValueKind == JsonValueKind.String
                ? statusValue.GetString()
                : null;
            if (status is not null && status != "completed")
            {
                var reason = root.TryGetProperty("incomplete_details", out var details) &&
                    details.ValueKind == JsonValueKind.Object &&
                    details.TryGetProperty("reason", out var reasonValue) &&
                    reasonValue.ValueKind == JsonValueKind.String
                    ? reasonValue.GetString()
                    : null;
                logger.LogWarning("Title generation returned status {Status} ({Reason}) with {MaxOutputTokens} output tokens.",
                    status, reason ?? "unspecified", Math.Clamp(options.Value.MaxOutputTokens, 16, 128));
                return null;
            }
            if (root.TryGetProperty("output_text", out var text) &&
                text.ValueKind == JsonValueKind.String &&
                !string.IsNullOrWhiteSpace(text.GetString()))
            {
                return text.GetString();
            }
            if (root.TryGetProperty("output", out var output) && output.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in output.EnumerateArray())
                {
                    if (item.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var block in content.EnumerateArray())
                        {
                            if (block.TryGetProperty("text", out text) &&
                                text.ValueKind == JsonValueKind.String &&
                                !string.IsNullOrWhiteSpace(text.GetString()))
                            {
                                return text.GetString();
                            }
                        }
                    }
                }
            }
            logger.LogWarning("Title generation returned no text (status {Status}).", status ?? "unspecified");
            return null;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Title generation failed.");
            return null;
        }
    }
}
