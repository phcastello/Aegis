using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using Aegis.Application.Voice;
using Aegis.Infrastructure.Models;
using Aegis.Infrastructure.Titles;
using Aegis.Infrastructure.Voice;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Aegis.Application.Tests;

public sealed class HandsFreeUtteranceClassifierTests
{
    [Theory]
    [InlineData("ignore", true)]
    [InlineData("attend", false)]
    [InlineData("ignore, sem dúvida", false)]
    [InlineData("IGNORE", false)]
    public async Task OnlyExactDecisionIgnoresAndRequestHasNoToolsOrStorage(string answer, bool expected)
    {
        using var client = new HttpClient(new Handler(async (request, ct) =>
        {
            Assert.Equal(new Uri("https://classification.example/v1/responses"), request.RequestUri);
            using var payload = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            var root = payload.RootElement;
            Assert.Equal("title-model", root.GetProperty("model").GetString());
            Assert.Equal("none", root.GetProperty("reasoning").GetProperty("effort").GetString());
            Assert.False(root.GetProperty("store").GetBoolean());
            Assert.False(root.TryGetProperty("tools", out _));
            Assert.Equal(16, root.GetProperty("max_output_tokens").GetInt32());
            var input = root.GetProperty("input");
            Assert.Equal("developer", input[0].GetProperty("role").GetString());
            Assert.Equal("user", input[1].GetProperty("role").GetString());
            using var data = JsonDocument.Parse(input[1].GetProperty("content").GetString()!);
            Assert.Equal("Leia os eventos da agenda", data.RootElement.GetProperty("transcript").GetString());
            Assert.Equal("Quer que eu leia?", data.RootElement.GetProperty("context")[0].GetProperty("content").GetString());
            return Output(answer);
        })) { BaseAddress = new Uri("https://classification.example/") };
        Assert.Equal(expected, await Classifier(client).ShouldIgnoreAsync("Leia os eventos da agenda", [new("assistant", "Quer que eu leia?")]));
    }

    [Theory]
    [InlineData("incomplete")]
    [InlineData("failed")]
    public async Task IncompleteProviderOutputDoesNotSuppressSpeech(string status)
    {
        using var client = new HttpClient(new Handler((_, _) => Task.FromResult(Output("ignore", status))))
        { BaseAddress = new Uri("https://classification.example/") };
        Assert.False(await Classifier(client).ShouldIgnoreAsync("João pega as chaves ali", []));
    }

    [Fact]
    public async Task ProviderErrorAndUnexpectedBodyFallBackToAttend()
    {
        foreach (var status in new[] { HttpStatusCode.ServiceUnavailable, HttpStatusCode.OK })
        {
            using var client = new HttpClient(new Handler((_, _) => Task.FromResult(new HttpResponseMessage(status)
            { Content = new StringContent("invalid output") }))) { BaseAddress = new Uri("https://classification.example/") };
            Assert.False(await Classifier(client).ShouldIgnoreAsync("João pega as chaves ali", []));
        }
    }

    [Fact]
    public async Task TimeoutFallsBackToAttendButClientCancellationPropagates()
    {
        using var client = new HttpClient(new Handler(async (_, ct) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return Output("ignore");
        })) { BaseAddress = new Uri("https://classification.example/") };
        using var cancelled = new CancellationTokenSource(TimeSpan.FromMilliseconds(20));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Classifier(client).ShouldIgnoreAsync("João pega as chaves ali", [], cancelled.Token));
        Assert.False(await Classifier(client).ShouldIgnoreAsync("João pega as chaves ali", []));
    }

    [Theory]
    [InlineData("Para aí.")]
    [InlineData("Sim")]
    [InlineData("Não, a outra")]
    [InlineData("Pode continuar")]
    [InlineData("What about yesterday?")]
    public async Task ShortUtteranceAlwaysAttendsWithoutHttp(string utterance)
    {
        var requests = 0;
        using var client = new HttpClient(new Handler((_, _) =>
        {
            requests++;
            return Task.FromResult(Output("ignore"));
        })) { BaseAddress = new Uri("https://classification.example/") };
        Assert.False(await Classifier(client).ShouldIgnoreAsync(utterance, []));
        Assert.Equal(0, requests);
    }

    private static OpenAiHandsFreeUtteranceClassifier Classifier(HttpClient client) =>
        new(client, Options.Create(new TitleOptions { Model = "title-model", ReasoningEffort = "none" }),
            Options.Create(new OpenAIOptions { ApiKey = "fake" }), NullLogger<OpenAiHandsFreeUtteranceClassifier>.Instance);

    private static HttpResponseMessage Output(string text, string status = "completed") => new(HttpStatusCode.OK)
    {
        Content = new StringContent(JsonSerializer.Serialize(new
        {
            status,
            output = new[] { new { type = "message", role = "assistant", content = new[] { new { type = "output_text", text } } } }
        }), Encoding.UTF8, "application/json")
    };

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handle) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            handle(request, cancellationToken);
    }
}
