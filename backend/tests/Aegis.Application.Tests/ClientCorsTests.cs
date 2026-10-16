using Aegis.Api;
using Xunit;
namespace Aegis.Application.Tests;
public class ClientCorsTests
{
    [Fact]
    public void DevelopmentOriginsAreExplicitNormalizedAndDeduplicated()
    {
        Assert.Empty(ClientCors.AdditionalOrigins(null));
        Assert.Equal(new[] { "http://192.0.2.10:1420", "https://dev.example.test" },
            ClientCors.AdditionalOrigins(" http://192.0.2.10:1420/ ; https://dev.example.test ; https://dev.example.test/ "));
    }
    [Theory]
    [InlineData("*")]
    [InlineData("https://*.example.test")]
    [InlineData("https://user:secret@example.test")]
    [InlineData("https://example.test/api")]
    [InlineData("https://example.test?token=x")]
    [InlineData("file:///tmp/client")]
    public void WildcardsCredentialsAndNonOriginsAreRejected(string origin) =>
        Assert.Throws<InvalidOperationException>(() => ClientCors.AdditionalOrigins(origin));
}
