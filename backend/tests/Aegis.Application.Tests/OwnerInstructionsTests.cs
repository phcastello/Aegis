using Aegis.Application.Prompts;
using Aegis.Application.Runtime;
using Aegis.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Aegis.Application.Tests;

public sealed class OwnerInstructionsTests
{
    [Fact]
    public async Task AnotherDeviceSeesSavedPreferenceAndStaleDraftCannotRestoreAClearedValue()
    {
        var database = new DbContextOptionsBuilder<AegisDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        await using var desktop = new AegisDbContext(database);
        await desktop.Database.EnsureCreatedAsync();
        var settings = new OwnerInstructionsStore(desktop);
        var initial = await settings.ReadAsync(default);
        Assert.Empty(initial.Text);
        const string preference = "Prefiro temperaturas em Celsius.";
        var saved = await settings.UpdateAsync(preference, initial.Revision, default);
        Assert.NotNull(saved);
        await using var phone = new AegisDbContext(database);
        var phoneSettings = new OwnerInstructionsStore(phone);
        Assert.Equal(saved, await phoneSettings.ReadAsync(default));
        var builder = new PromptBuilder(new NoRuntime(), phoneSettings);
        var before = await builder.BuildPromptAsync([], "Quanto dá em Fahrenheit?");
        Assert.Equal("user", before.InputItems[1].GetProperty("role").GetString());
        Assert.Contains(preference, before.InputItems[1].GetProperty("content").GetString());
        Assert.Equal("Quanto dá em Fahrenheit?", before.InputItems[2].GetProperty("content").GetString());
        Assert.DoesNotContain(preference, before.Prompt);
        Assert.DoesNotContain(before.AuditInputItems!, item => item.GetRawText().Contains("Celsius"));
        var cleared = await phoneSettings.UpdateAsync("", saved!.Revision, default);
        Assert.Equal("", cleared?.Text);
        Assert.Null(await settings.UpdateAsync("Uma edição já ultrapassada.", saved.Revision, default));
        var after = await builder.BuildPromptAsync([], "Outra pergunta");
        Assert.Equal(2, after.InputItems.Count);
        Assert.DoesNotContain(after.InputItems, item => item.GetRawText().Contains("Celsius"));
    }

    private sealed class NoRuntime : IRuntimeContextProvider
    {
        public Task<string> GetRuntimeContextAsync(CancellationToken cancellationToken = default) => Task.FromResult("");
    }
}
