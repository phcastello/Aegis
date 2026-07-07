namespace Aegis.Application.Chat;

public interface ILocalTitleGenerator
{
    Task<string?> GenerateAsync(
        string userContent,
        string assistantContent,
        CancellationToken cancellationToken = default);
}
