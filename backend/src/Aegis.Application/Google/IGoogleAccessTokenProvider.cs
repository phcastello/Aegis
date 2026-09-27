namespace Aegis.Application.Google;

public interface IGoogleAccessTokenProvider
{
    Task<string> GetAccessTokenAsync(string requiredScope, CancellationToken cancellationToken = default);
}

public static class GoogleScopes
{
    public const string Gmail = "https://www.googleapis.com/auth/gmail.modify";
    public const string Calendar = "https://www.googleapis.com/auth/calendar.events";
    public const string CalendarList = "https://www.googleapis.com/auth/calendar.calendarlist.readonly";
    public const string Combined = Gmail + " " + Calendar + " " + CalendarList;

    public static bool Contains(string? scopes, string scope) =>
        (scopes ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries).Contains(scope, StringComparer.Ordinal);

    public static string EnsureRequired(string? scopes) => string.Join(' ',
        (scopes ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries).Concat([Gmail, Calendar, CalendarList]).Distinct(StringComparer.Ordinal));
}

public sealed class GoogleScopeMissingException(string scope) : Exception("Google authorization is missing a required scope.")
{
    public string Scope { get; } = scope;
}
