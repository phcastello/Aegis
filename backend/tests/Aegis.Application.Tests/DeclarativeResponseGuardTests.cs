using Aegis.Application.Chat;
using Xunit;

namespace Aegis.Application.Tests;

public sealed class DeclarativeResponseGuardTests
{
    [Theory]
    [InlineData("Meu PC tinha 16 GB de RAM.",
        "Isso indica que seu PC tinha 16 GB de memória RAM.", "Certo.")]
    [InlineData("A Aegis usa PostgreSQL como fonte canônica do sistema de memória.",
        "Entendi: o PostgreSQL é a fonte canônica do sistema de memória da Aegis.", "Entendi.")]
    [InlineData("Troquei a memória do PC e agora ele tem 32 GB de RAM.",
        "Entendi — agora o PC tem 32 GB de RAM.", "Entendi.")]
    [InlineData("Meu monitor é 4K 144 Hz.", "Agora seu monitor é 4K 144 Hz.", "Certo.")]
    public void RemovesObviousRestatements(string user, string assistant, string expected) =>
        Assert.Equal(expected, DeclarativeResponseGuard.Normalize(user, assistant));

    [Theory]
    [InlineData("Troquei a memória do PC e agora ele tem 32 GB de RAM.",
        "Então os 32 GB atuais foram um upgrade de 2×.")]
    [InlineData("Na faculdade me chamam de Vecna.",
        "Apelido de respeito — ou uma avaliação acadêmica bastante específica.")]
    [InlineData("Depois da FaZe, meu time de R6 favorito é a DarkZero.", "Boa combinação.")]
    [InlineData("Esquece essa informação sobre a DarkZero.", "Apaguei essa informação.")]
    [InlineData("Quanto de RAM meu PC tem hoje?", "Seu PC tem 32 GB de RAM.")]
    public void PreservesUsefulCommentsAndRequestedActions(string user, string assistant) =>
        Assert.Equal(assistant, DeclarativeResponseGuard.Normalize(user, assistant));
}
