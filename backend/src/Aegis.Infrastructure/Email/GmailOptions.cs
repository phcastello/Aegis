namespace Aegis.Infrastructure.Email;

public sealed class GmailOptions
{
    public const string DefaultProvider = "gmail";
    public const string DefaultScope = "https://www.googleapis.com/auth/gmail.modify";

    public string? ClientId { get; set; }

    public string? ClientSecret { get; set; }

    public string? RedirectUri { get; set; }

    public string Scopes { get; set; } = DefaultScope;

    public string SuccessRedirectPath { get; set; } = "/?email=connected";

    public string FailureRedirectPath { get; set; } = "/?email=connect_failed";

    public int MaxEmailsPerManualBriefing { get; set; } = 30;

    public int MaxEmailsToReadPerBriefing { get; set; } = 15;

    public int MaxEmailBriefingBodyChars { get; set; } = 500;

    public int MaxEmailFullBodyChars { get; set; } = 50000;

    public int MaxEmailBodyChars { get; set; } = 6000;

    public int EmailBriefingLookbackDays { get; set; } = 7;
}
