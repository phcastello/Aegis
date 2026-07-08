using Aegis.Application.Common;
using Aegis.Application.Chat;
using Aegis.Application.Email;
using Aegis.Application.Prompts;
using Aegis.Application.Models;
using Aegis.Infrastructure.Chat;
using Aegis.Infrastructure.Email;
using Aegis.Infrastructure.Models;
using Aegis.Infrastructure.Persistence;
using Aegis.Infrastructure.Titles;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Aegis.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString("AegisDatabase")
            ?? throw new InvalidOperationException("Connection string 'AegisDatabase' was not found.");

        services.AddDbContext<AegisDbContext>(options =>
            options.UseNpgsql(connectionString));

        services.AddScoped<IAegisDbContext>(provider => provider.GetRequiredService<AegisDbContext>());
        services.AddDataProtection()
            .SetApplicationName("Aegis");

        services.Configure<OpenAIOptions>(options =>
        {
            configuration.GetSection(OpenAIOptions.SectionName).Bind(options);
            options.ApiKey = Read(configuration, "OPENAI_API_KEY", options.ApiKey);
            options.ModelProvider = Read(configuration, "AEGIS_MODEL_PROVIDER", options.ModelProvider);
            options.DefaultModel = Read(configuration, "AEGIS_DEFAULT_MODEL", options.DefaultModel);
            options.MainModel = Read(configuration, "AEGIS_MAIN_MODEL", options.MainModel);
            options.EscalationModel = Read(configuration, "AEGIS_ESCALATION_MODEL", options.EscalationModel);
            options.ServiceTier = Read(configuration, "AEGIS_OPENAI_SERVICE_TIER", options.ServiceTier);
            options.UseEscalationAutomatically = ReadBool(
                configuration,
                "AEGIS_USE_ESCALATION_AUTOMATICALLY",
                options.UseEscalationAutomatically);
            options.StoreResponses = ReadBool(
                configuration,
                "AEGIS_OPENAI_STORE_RESPONSES",
                options.StoreResponses);
            options.MaxOutputTokens = ReadInt(
                configuration,
                "AEGIS_MAX_OUTPUT_TOKENS",
                options.MaxOutputTokens);
            options.WebSearchEnabled = ReadBool(
                configuration,
                "AEGIS_WEB_SEARCH_ENABLED",
                options.WebSearchEnabled);
            options.WebSearchRequireExplicitRequest = ReadBool(
                configuration,
                "AEGIS_WEB_SEARCH_REQUIRE_EXPLICIT_REQUEST",
                options.WebSearchRequireExplicitRequest);
        });
        services.AddHttpClient<IAegisModelClient, OpenAIResponsesClient>((provider, client) =>
        {
            var options = provider.GetRequiredService<IOptions<OpenAIOptions>>().Value;
            var baseUrl = string.IsNullOrWhiteSpace(options.BaseUrl)
                ? OpenAIOptions.DefaultBaseUrl
                : options.BaseUrl;

            client.BaseAddress = new Uri(baseUrl);
            client.Timeout = TimeSpan.FromMinutes(5);
        });

        services.Configure<GmailOptions>(options =>
        {
            options.ClientId = ReadAny(configuration, options.ClientId, "GOOGLE_CLIENT_ID", "Google:ClientId");
            options.ClientSecret = ReadAny(configuration, options.ClientSecret, "GOOGLE_CLIENT_SECRET", "Google:ClientSecret");
            options.RedirectUri = ReadAny(configuration, options.RedirectUri, "GOOGLE_REDIRECT_URI", "Google:RedirectUri");
            options.Scopes = ReadAny(configuration, options.Scopes, "GOOGLE_OAUTH_SCOPES", "Google:OAuthScopes");
            options.SuccessRedirectPath = ReadAny(
                configuration,
                options.SuccessRedirectPath,
                "GOOGLE_OAUTH_SUCCESS_REDIRECT_PATH",
                "Google:OAuthSuccessRedirectPath");
            options.FailureRedirectPath = ReadAny(
                configuration,
                options.FailureRedirectPath,
                "GOOGLE_OAUTH_FAILURE_REDIRECT_PATH",
                "Google:OAuthFailureRedirectPath");
            options.MaxEmailsPerManualBriefing = ReadIntAny(
                configuration,
                options.MaxEmailsPerManualBriefing,
                "AEGIS_MAX_EMAILS_PER_MANUAL_BRIEFING",
                "Aegis:MaxEmailsPerManualBriefing");
            options.MaxEmailsToReadPerBriefing = ReadIntAny(
                configuration,
                options.MaxEmailsToReadPerBriefing,
                "AEGIS_MAX_EMAILS_TO_READ_PER_BRIEFING",
                "Aegis:MaxEmailsToReadPerBriefing");
            options.MaxEmailBriefingBodyChars = ReadIntAny(
                configuration,
                options.MaxEmailBriefingBodyChars,
                "AEGIS_MAX_EMAIL_BRIEFING_BODY_CHARS",
                "Aegis:MaxEmailBriefingBodyChars");
            options.MaxEmailFullBodyChars = ReadIntAny(
                configuration,
                options.MaxEmailFullBodyChars,
                "AEGIS_MAX_EMAIL_FULL_BODY_CHARS",
                "Aegis:MaxEmailFullBodyChars",
                "AEGIS_MAX_EMAIL_BODY_CHARS",
                "Aegis:MaxEmailBodyChars");
            options.MaxEmailBodyChars = ReadIntAny(
                configuration,
                options.MaxEmailBodyChars,
                "AEGIS_MAX_EMAIL_BODY_CHARS",
                "Aegis:MaxEmailBodyChars");
            options.EmailBriefingLookbackDays = ReadIntAny(
                configuration,
                options.EmailBriefingLookbackDays,
                "AEGIS_EMAIL_BRIEFING_LOOKBACK_DAYS",
                "Aegis:EmailBriefingLookbackDays");
        });
        services.AddSingleton<IEmailPromptSettings>(provider =>
        {
            var options = provider.GetRequiredService<IOptions<GmailOptions>>().Value;
            return new EmailPromptSettings(
                options.MaxEmailsPerManualBriefing,
                options.MaxEmailsToReadPerBriefing,
                options.MaxEmailBriefingBodyChars,
                options.MaxEmailFullBodyChars);
        });
        services.AddSingleton<EmailTokenProtector>();
        services.AddHttpClient<IEmailConnectionService, GmailConnectionService>();
        services.AddHttpClient<IEmailService, GmailService>();

        services.Configure<LocalTitleOptions>(options =>
        {
            options.Provider = Read(configuration, "AEGIS_TITLE_PROVIDER", options.Provider);
            options.LocalBaseUrl = Read(configuration, "AEGIS_TITLE_LOCAL_BASE_URL", options.LocalBaseUrl);
            options.LocalModel = Read(configuration, "AEGIS_TITLE_LOCAL_MODEL", options.LocalModel);
            options.TimeoutSeconds = ReadInt(
                configuration,
                "AEGIS_TITLE_TIMEOUT_SECONDS",
                options.TimeoutSeconds);
            options.MaxOutputTokens = ReadInt(
                configuration,
                "AEGIS_TITLE_MAX_OUTPUT_TOKENS",
                options.MaxOutputTokens);
        });
        services.AddHttpClient<ILocalTitleGenerator, LocalTitleGenerator>((provider, client) =>
        {
            var options = provider.GetRequiredService<IOptions<LocalTitleOptions>>().Value;
            var baseUrl = string.IsNullOrWhiteSpace(options.LocalBaseUrl)
                ? LocalTitleOptions.DefaultBaseUrl
                : options.LocalBaseUrl;

            client.BaseAddress = new Uri(baseUrl);
            client.Timeout = TimeSpan.FromSeconds(Math.Max(1, options.TimeoutSeconds + 2));
        });
        services.AddHostedService<ConversationTitleWorker>();

        return services;
    }

    private static string Read(IConfiguration configuration, string key, string? fallback)
    {
        var value = configuration[key];
        return string.IsNullOrWhiteSpace(value)
            ? fallback ?? string.Empty
            : value.Trim();
    }

    private static bool ReadBool(IConfiguration configuration, string key, bool fallback)
    {
        return bool.TryParse(configuration[key], out var value)
            ? value
            : fallback;
    }

    private static int ReadInt(IConfiguration configuration, string key, int fallback)
    {
        return int.TryParse(configuration[key], out var value)
            ? value
            : fallback;
    }

    private static string ReadAny(IConfiguration configuration, string? fallback, params string[] keys)
    {
        foreach (var key in keys)
        {
            var value = configuration[key];
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value.Trim();
            }
        }

        return fallback ?? string.Empty;
    }

    private static int ReadIntAny(IConfiguration configuration, int fallback, params string[] keys)
    {
        foreach (var key in keys)
        {
            if (int.TryParse(configuration[key], out var value))
            {
                return value;
            }
        }

        return fallback;
    }
}
