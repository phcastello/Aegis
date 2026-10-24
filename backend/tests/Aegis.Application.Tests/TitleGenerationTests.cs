using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Aegis.Application.Chat;
using Aegis.Infrastructure.Chat;
using Aegis.Infrastructure.Models;
using Aegis.Infrastructure.Titles;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Aegis.Application.Tests;

public sealed class TitleGenerationTests
{
    [Fact]
    public async Task TitleRequestUsesConfiguredModelMinimalInputAndNoTools()
    {
        var handler = new TitleHandler();
        using var client = new HttpClient(handler) { BaseAddress = new Uri("https://api.openai.com/") };
        var generator = new OpenAiTitleGenerator(client,
            Options.Create(new TitleOptions { Model = "custom-title-model" }),
            Options.Create(new OpenAIOptions { ApiKey = "fake" }),
            NullLogger<OpenAiTitleGenerator>.Instance);

        var title = await generator.GenerateAsync("Pergunta curta", "Resposta curta");

        Assert.Equal("Título produzido", title);
        Assert.Equal("Bearer fake", handler.Authorization);
        using var payload = JsonDocument.Parse(handler.Body!);
        Assert.Equal("custom-title-model", payload.RootElement.GetProperty("model").GetString());
        Assert.Equal("minimal", payload.RootElement.GetProperty("reasoning").GetProperty("effort").GetString());
        Assert.False(payload.RootElement.TryGetProperty("tools", out _));
        Assert.False(payload.RootElement.TryGetProperty("instructions", out _));
        Assert.Equal(2, payload.RootElement.GetProperty("input").GetArrayLength());
        var policy = payload.RootElement.GetProperty("input")[0].GetProperty("content").GetString()!;
        Assert.Contains("Comprar leite na saída", policy);
        Assert.Contains("prefixos classificatórios", policy);
        Assert.Contains("nomeie a própria tarefa", policy);
        Assert.Contains("Lembrete para", policy);
        Assert.Contains("próprio sistema de lembretes", policy);
        Assert.DoesNotContain("runtime", handler.Body!, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("gpt-5-nano", new TitleOptions().Model);
    }

    [Fact]
    public async Task TitleProviderErrorIsContainedAndExistingSanitizerStillApplies()
    {
        var handler = new TitleHandler { Fail = true };
        using var client = new HttpClient(handler) { BaseAddress = new Uri("https://api.openai.com/") };
        var generator = new OpenAiTitleGenerator(client, Options.Create(new TitleOptions()),
            Options.Create(new OpenAIOptions { ApiKey = "fake" }), NullLogger<OpenAiTitleGenerator>.Instance);

        Assert.Null(await generator.GenerateAsync("x", "y"));
        var sanitizer = typeof(ConversationTitleService).GetMethod("SanitizeGeneratedTitle", BindingFlags.NonPublic | BindingFlags.Static)!;
        Assert.Equal("Assunto", sanitizer.Invoke(null, ["Título: \"Assunto\".\nOutra linha"]));
    }

    [Fact]
    public async Task TitleWorkerRunsQueuedJobAfterEnqueueReturns()
    {
        var queue = new ConversationTitleJobQueue();
        var service = new BlockingTitleService();
        using var provider = new ServiceCollection()
            .AddSingleton<IConversationTitleService>(service)
            .BuildServiceProvider();
        var worker = new ConversationTitleWorker(queue, provider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<ConversationTitleWorker>.Instance);
        await worker.StartAsync(CancellationToken.None);
        try
        {
            var job = new ConversationTitleJob(Guid.NewGuid(), "pergunta", "resposta");
            await queue.EnqueueAsync(job);
            Assert.Equal(job, await service.Started.Task.WaitAsync(TimeSpan.FromSeconds(3)));
            service.Release.SetResult();
        }
        finally
        {
            service.Release.TrySetResult();
            await worker.StopAsync(CancellationToken.None);
        }
    }

    [Theory]
    [InlineData("Comprar leite na saída")]
    [InlineData("Limpar a caixa da Kali")]
    [InlineData("Protocolo ao chegar em casa")]
    [InlineData("Quem é a Aegis?")]
    [InlineData("Como funcionam os lembretes")]
    public void NaturalTitlesArePreservedAndManualRenameWins(string title)
    {
        var sanitizer = typeof(ConversationTitleService).GetMethod("SanitizeGeneratedTitle", BindingFlags.NonPublic | BindingFlags.Static)!;
        Assert.Equal(title, sanitizer.Invoke(null, [title]));
        var conversation = new Aegis.Domain.Entities.Conversation();
        conversation.Rename("Meu título");
        conversation.SetGeneratedTitle(title);
        Assert.Equal("Meu título", conversation.Title);
        Assert.False(conversation.CanGenerateAutomaticTitle);
    }

    private sealed class LiveTitleFactAttribute : FactAttribute {
        public LiveTitleFactAttribute() {
            if (Environment.GetEnvironmentVariable("AEGIS_TITLE_TEST_OPENAI") != "YES" || string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("OPENAI_API_KEY")))
                Skip = "Requires explicit AEGIS_TITLE_TEST_OPENAI=YES and a model API key; no application data is written.";
        }
    }
    [LiveTitleFact]
    public async Task LiveModelNamesTheTaskRatherThanTheReminderRequest() {
        using var http = new HttpClient { BaseAddress = new Uri(Environment.GetEnvironmentVariable("AEGIS_OPENAI_BASE_URL") ?? "https://api.openai.com/"), Timeout = TimeSpan.FromSeconds(32) };
        var generator = new OpenAiTitleGenerator(http,
            Options.Create(new TitleOptions { Model = Environment.GetEnvironmentVariable("AEGIS_TITLE_MODEL") ?? TitleOptions.DefaultModel }),
            Options.Create(new OpenAIOptions { ApiKey = Environment.GetEnvironmentVariable("OPENAI_API_KEY") }), NullLogger<OpenAiTitleGenerator>.Instance);
        foreach (var request in new[] { "Me lembra de comprar leite ao sair da faculdade", "Me lembra de limpar a caixa da Kali", "Me lembra do protocolo ao chegar em casa", "Me lembra de levar o guarda-chuva quando sair do trabalho", "Quem é a Aegis?", "Como funciona o sistema de lembretes da Aegis?" }) {
            var operational = request.StartsWith("Me lembra", StringComparison.Ordinal);
            var title = await generator.GenerateAsync(request, operational ? "Criei o lembrete solicitado." : "Sou a Aegis. Tenho lembretes temporais e geográficos.");
            Assert.False(string.IsNullOrWhiteSpace(title), "The configured title model must return completed text.");
            Assert.InRange(title!.Trim().Length, 1, 50);
            if (!operational) continue; // The reminder system itself may legitimately be the topic.
            foreach (var prefix in new[] { "Lembrete:", "Lembrete para", "Lembrete de", "Lembrar de", "Pedido de", "Criação de lembrete" })
                Assert.False(title.Trim().StartsWith(prefix, StringComparison.OrdinalIgnoreCase), $"Unnatural task title: {title}");
        }
    }

    private sealed class BlockingTitleService : IConversationTitleService
    {
        public TaskCompletionSource<ConversationTitleJob> Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task GenerateTitleAsync(ConversationTitleJob job, CancellationToken cancellationToken = default)
        {
            Started.TrySetResult(job);
            await Release.Task.WaitAsync(cancellationToken);
        }
    }

    private sealed class TitleHandler : HttpMessageHandler
    {
        public bool Fail { get; set; }
        public string? Body { get; private set; }
        public string? Authorization { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Body = await request.Content!.ReadAsStringAsync(cancellationToken);
            Authorization = request.Headers.Authorization?.ToString();
            return new HttpResponseMessage(Fail ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK)
            {
                Content = new StringContent("{\"output_text\":\"Título produzido\"}", Encoding.UTF8, "application/json")
            };
        }
    }
}
