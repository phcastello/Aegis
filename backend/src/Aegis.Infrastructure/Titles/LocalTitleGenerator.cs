using Aegis.Application.Chat;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Aegis.Infrastructure.Titles;

public sealed class LocalTitleGenerator(
    HttpClient httpClient,
    IOptions<LocalTitleOptions> options,
    ILogger<LocalTitleGenerator> logger) : ILocalTitleGenerator
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public async Task<string?> GenerateAsync(
        string userContent,
        string assistantContent,
        CancellationToken cancellationToken = default)
    {
        var titleOptions = options.Value;
        if (!string.Equals(titleOptions.Provider, "local", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, titleOptions.TimeoutSeconds)));

        var payload = new
        {
            model = string.IsNullOrWhiteSpace(titleOptions.LocalModel)
                ? LocalTitleOptions.DefaultModel
                : titleOptions.LocalModel.Trim(),
            prompt = BuildPrompt(userContent, assistantContent),
            stream = false,
            options = new
            {
                temperature = 0.1,
                num_predict = Math.Max(1, titleOptions.MaxOutputTokens)
            }
        };

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "api/generate")
            {
                Content = new StringContent(
                    JsonSerializer.Serialize(payload, JsonOptions),
                    Encoding.UTF8,
                    "application/json")
            };
            using var response = await httpClient.SendAsync(request, timeout.Token);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning(
                    "Local title generation failed with HTTP {StatusCode}.",
                    (int)response.StatusCode);
                return null;
            }

            var responseBody = await response.Content.ReadAsStringAsync(timeout.Token);
            var titleResponse = JsonSerializer.Deserialize<LocalGenerateResponse>(responseBody, JsonOptions);
            return titleResponse?.Response;
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning("Local title generation timed out after {TimeoutSeconds} seconds.", titleOptions.TimeoutSeconds);
            return null;
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Local title generation failed.");
            return null;
        }
    }

    private static string BuildPrompt(string userContent, string assistantContent)
    {
        return $"""
            Você gera títulos curtos para conversas da Aegis.

            Tarefa:
            - Leia a primeira mensagem de Pedro e a primeira resposta da Aegis.
            - Extraia o assunto central da conversa.
            - Escreva um título nominal curto, como nome de tópico.

            Regras obrigatórias:
            - Responda somente com o título.
            - Português brasileiro.
            - Máximo de 50 caracteres.
            - Sem aspas.
            - Sem ponto final.
            - Sem emoji.
            - Não use "conversa", "pergunta", "resposta", "explicação", "ajuda" ou frases genéricas.
            - Não responda a Pedro.
            - Não invente outra pergunta.
            - Não reformule a conversa como uma pergunta.
            - Não mude o assunto.
            - Nomeie o assunto real discutido no par mensagem/resposta.
            - Se o assunto for vago, use um título simples e neutro.
            - Não incluir instruções de geração de título na resposta.
            - Nunca comece com "Título:".
            - Se a primeira mensagem for uma saudação ou check-in, responda: Saudação inicial

            Exemplos:
            Pedro: Qual você diria que é sua dádiva como Aegis?
            Aegis: Minha dádiva como Aegis é transformar conversa em continuidade útil...
            Título: Dádiva da Aegis

            Pedro: Você pode resumir meus emails importantes?
            Aegis: Posso resumir os emails e destacar prioridades...
            Título: Resumo de emails importantes

            Pedro: Como eu configuro OAuth do Google?
            Aegis: Para configurar OAuth do Google, crie um client...
            Título: Configuração de OAuth do Google

            Pedro: Oi
            Aegis: Olá, Pedro. Como posso ajudar?
            Título: Saudação inicial

            Primeira mensagem de Pedro:
            ```
            {userContent.Trim()}
            ```

            Primeira resposta da Aegis:
            ```
            {assistantContent.Trim()}
            ```
            """;
    }

    private sealed class LocalGenerateResponse
    {
        [JsonPropertyName("response")]
        public string? Response { get; init; }
    }
}
