namespace Aegis.Application.Runtime;

public sealed class RuntimeContextProvider : IRuntimeContextProvider
{
    private static readonly TimeZoneInfo BrasiliaTimeZone = ResolveBrasiliaTimeZone();

    public Task<string> GetRuntimeContextAsync(CancellationToken cancellationToken = default)
    {
        var utcNow = DateTimeOffset.UtcNow;
        var brasiliaNow = TimeZoneInfo.ConvertTime(utcNow, BrasiliaTimeZone);

        var context = $"""
            Operational context, for background only:
            I am currently available through chat.
            Current Brasilia timestamp: {brasiliaNow:O}
            Current UTC timestamp: {utcNow:O}
            Reference timezone for date/time questions: {BrasiliaTimeZone.Id}
            If the conversation requires another timezone, convert from the Brasilia reference time.
            Use operational details only when directly relevant.
            Do not announce status or implementation details by default.
            """;

        return Task.FromResult(context);
    }

    private static TimeZoneInfo ResolveBrasiliaTimeZone()
    {
        return TryFindTimeZone("America/Sao_Paulo")
            ?? TryFindTimeZone("E. South America Standard Time")
            ?? throw new InvalidOperationException("Brasilia timezone could not be resolved on this host.");
    }

    private static TimeZoneInfo? TryFindTimeZone(string timeZoneId)
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);
        }
        catch (TimeZoneNotFoundException)
        {
            return null;
        }
        catch (InvalidTimeZoneException)
        {
            return null;
        }
    }
}
