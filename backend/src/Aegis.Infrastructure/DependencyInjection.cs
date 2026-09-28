using Aegis.Application.Reminders;
using Aegis.Infrastructure.Reminders;
using Aegis.Application.Common;
using Aegis.Application.Chat;
using Aegis.Application.Calendar;
using Aegis.Application.Google;
using Aegis.Infrastructure.Calendar;
using Aegis.Application.Email;
using Aegis.Application.Prompts;
using Aegis.Application.Models;
using Aegis.Infrastructure.Chat;
using Aegis.Infrastructure.Email;
using Aegis.Infrastructure.Models;
using Aegis.Infrastructure.Persistence;
using Aegis.Infrastructure.Titles;
using Aegis.Infrastructure.Voice;
using Aegis.Infrastructure.Voice.Transcription;
using Aegis.Application.Memory;
using Aegis.Infrastructure.Memory;
using Aegis.Application.Voice;
using Aegis.Application.Voice.Transcription;
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
            options.BaseUrl = Read(configuration, "AEGIS_OPENAI_BASE_URL", options.BaseUrl);
            options.ChatModel = Read(configuration, "AEGIS_CHAT_MODEL", options.ChatModel);
            options.ChatReasoningEffort = Read(configuration, "AEGIS_CHAT_REASONING_EFFORT", options.ChatReasoningEffort);
            options.ServiceTier = Read(configuration, "AEGIS_OPENAI_SERVICE_TIER", options.ServiceTier);
            options.StoreResponses = ReadBool(
                configuration,
                "AEGIS_OPENAI_STORE_RESPONSES",
                options.StoreResponses);
            options.MaxOutputTokens = ReadInt(
                configuration,
                "AEGIS_MAX_OUTPUT_TOKENS",
                options.MaxOutputTokens);
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

        services.Configure<AegisTtsOptions>(options =>
        {
            configuration.GetSection(AegisTtsOptions.SectionName).Bind(options);
            options.Enabled = ReadBool(configuration, "AEGIS_TTS_ENABLED", options.Enabled);
            options.BaseUrl = Read(configuration, "AEGIS_TTS_BASE_URL", options.BaseUrl);
            options.Profile = Read(configuration, "AEGIS_TTS_PROFILE", options.Profile);
            options.DefaultPriority = ReadInt(configuration, "AEGIS_TTS_DEFAULT_PRIORITY", options.DefaultPriority);
            options.ConnectTimeoutSeconds = ReadInt(configuration, "AEGIS_TTS_CONNECT_TIMEOUT_SECONDS", options.ConnectTimeoutSeconds);
            options.FirstAudioTimeoutSeconds = ReadInt(configuration, "AEGIS_TTS_FIRST_AUDIO_TIMEOUT_SECONDS", options.FirstAudioTimeoutSeconds);
            options.IdleStreamTimeoutSeconds = ReadInt(configuration, "AEGIS_TTS_IDLE_STREAM_TIMEOUT_SECONDS", options.IdleStreamTimeoutSeconds);
            options.ApiToken = Read(configuration, "AEGIS_TTS_API_TOKEN", options.ApiToken);
        });
        services.AddHttpClient<IAegisSpeechClient, AegisSpeechClient>((provider, client) =>
        {
            var tts = provider.GetRequiredService<IOptions<AegisTtsOptions>>().Value;
            client.BaseAddress = new Uri(tts.BaseUrl.TrimEnd('/') + "/");
            client.Timeout = Timeout.InfiniteTimeSpan;
        });

        services.Configure<SttOptions>(options =>
        {
            configuration.GetSection(SttOptions.SectionName).Bind(options);
            options.Enabled = ReadBool(configuration, "AEGIS_STT_ENABLED", options.Enabled);
            options.PrimaryProvider = Read(configuration, "AEGIS_STT_PRIMARY_PROVIDER", options.PrimaryProvider);
            options.FallbackEnabled = ReadBool(configuration, "AEGIS_STT_FALLBACK_ENABLED", options.FallbackEnabled);
            options.FallbackProvider = Read(configuration, "AEGIS_STT_FALLBACK_PROVIDER", options.FallbackProvider);
            options.Language = Read(configuration, "AEGIS_STT_LANGUAGE", options.Language);
            options.TimeoutSeconds = ReadInt(configuration, "AEGIS_STT_TIMEOUT_SECONDS", options.TimeoutSeconds);
            options.MaxAudioBytes = ReadInt(configuration, "AEGIS_STT_MAX_AUDIO_BYTES", options.MaxAudioBytes);
            options.MaxRecordingSeconds = ReadInt(configuration, "AEGIS_STT_MAX_RECORDING_SECONDS", options.MaxRecordingSeconds);
            options.MaxKeyterms = ReadInt(configuration, "AEGIS_STT_MAX_KEYTERMS", options.MaxKeyterms);
            options.Keyterms = Read(configuration, "AEGIS_STT_KEYTERMS", options.Keyterms);
        });
        services.Configure<ElevenLabsSttOptions>(options =>
        {
            options.ApiKey = Read(configuration, "AEGIS_STT_ELEVENLABS_API_KEY", options.ApiKey);
            options.Model = Read(configuration, "AEGIS_STT_ELEVENLABS_MODEL", options.Model);
            options.BaseUrl = Read(configuration, "AEGIS_STT_ELEVENLABS_BASE_URL", options.BaseUrl);
        });
        services.Configure<OpenAiSttOptions>(options =>
        {
            // Deliberately only reads AEGIS_STT_OPENAI_API_KEY. Never use OPENAI_API_KEY here.
            options.ApiKey = Read(configuration, "AEGIS_STT_OPENAI_API_KEY", options.ApiKey);
            options.Model = Read(configuration, "AEGIS_STT_OPENAI_MODEL", options.Model);
            options.BaseUrl = Read(configuration, "AEGIS_STT_OPENAI_BASE_URL", options.BaseUrl);
        });
        services.AddSingleton<ISttKeytermProvider, SttKeytermProvider>();
        services.AddHttpClient<ElevenLabsTranscriptionClient>((provider, client) =>
        {
            var stt = provider.GetRequiredService<IOptions<ElevenLabsSttOptions>>().Value;
            client.BaseAddress = new Uri(stt.BaseUrl.TrimEnd('/') + "/");
            client.Timeout = Timeout.InfiniteTimeSpan;
        });
        services.AddHttpClient<OpenAiTranscriptionClient>((provider, client) =>
        {
            var stt = provider.GetRequiredService<IOptions<OpenAiSttOptions>>().Value;
            client.BaseAddress = new Uri(stt.BaseUrl.TrimEnd('/') + "/");
            client.Timeout = Timeout.InfiniteTimeSpan;
        });
        services.AddScoped<ISpeechTranscriptionProvider>(provider => provider.GetRequiredService<ElevenLabsTranscriptionClient>());
        services.AddScoped<ISpeechTranscriptionProvider>(provider => provider.GetRequiredService<OpenAiTranscriptionClient>());
        services.AddScoped<ITranscriptionSettings>(provider => provider.GetRequiredService<IOptions<SttOptions>>().Value);

        services.Configure<GmailOptions>(options =>
        {
            options.ClientId = ReadAny(configuration, options.ClientId, "GOOGLE_CLIENT_ID", "Google:ClientId");
            options.ClientSecret = ReadAny(configuration, options.ClientSecret, "GOOGLE_CLIENT_SECRET", "Google:ClientSecret");
            options.RedirectUri = ReadAny(configuration, options.RedirectUri, "GOOGLE_REDIRECT_URI", "Google:RedirectUri");
            options.PublicAppUrl = ReadAny(configuration, options.PublicAppUrl, "AEGIS_PUBLIC_APP_URL", "Google:PublicAppUrl");
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
                "Aegis:MaxEmailFullBodyChars");
            options.EmailBriefingLookbackDays = ReadIntAny(
                configuration,
                options.EmailBriefingLookbackDays,
                "AEGIS_EMAIL_BRIEFING_LOOKBACK_DAYS",
                "Aegis:EmailBriefingLookbackDays");
        });
        services.AddSingleton<EmailTokenProtector>();
        services.AddHttpClient<IEmailConnectionService, GmailConnectionService>();
        services.AddHttpClient<IGoogleAccessTokenProvider, GoogleAccessTokenProvider>();
        services.AddHttpClient<IEmailService, GmailService>();
        services.AddOptions<GoogleCalendarOptions>()
            .Configure(options =>
            {
                var section = configuration.GetSection(GoogleCalendarOptions.SectionName);
                options.TimedEventDefaultReminders = ReminderMinutes("AEGIS_CALENDAR_TIMED_REMINDERS",
                    section.GetSection(nameof(options.TimedEventDefaultReminders)).Get<int[]>() ?? options.TimedEventDefaultReminders);
                options.AllDayDefaultReminders = ReminderMinutes("AEGIS_CALENDAR_ALL_DAY_REMINDERS",
                    section.GetSection(nameof(options.AllDayDefaultReminders)).Get<int[]>() ?? options.AllDayDefaultReminders);
            })
            .Validate(options => GoogleCalendarOptions.ValidMinutes(options.TimedEventDefaultReminders) &&
                GoogleCalendarOptions.ValidMinutes(options.AllDayDefaultReminders),
                "Calendar reminder defaults require at most five distinct minute values between 0 and 40320.")
            .ValidateOnStart();
        services.AddHttpClient<ICalendarService, GoogleCalendarService>();

        int[] ReminderMinutes(string key, int[] fallback)
        {
            var value = configuration[key];
            if (string.IsNullOrWhiteSpace(value)) return fallback;
            var parts = value.Split(',', StringSplitOptions.TrimEntries);
            if (parts.Any(part => !int.TryParse(part, out _)))
                throw new OptionsValidationException(key, typeof(GoogleCalendarOptions), ["Use comma-separated integer minute values."]);
            return parts.Select(int.Parse).ToArray();
        }

        services.Configure<TitleOptions>(options =>
        {
            configuration.GetSection(TitleOptions.SectionName).Bind(options);
            options.Model = Read(configuration, "AEGIS_TITLE_MODEL", options.Model);
            options.ReasoningEffort = Read(configuration, "AEGIS_TITLE_REASONING_EFFORT", options.ReasoningEffort);
            options.TimeoutSeconds = ReadInt(
                configuration,
                "AEGIS_TITLE_TIMEOUT_SECONDS",
                options.TimeoutSeconds);
            options.MaxOutputTokens = ReadInt(
                configuration,
                "AEGIS_TITLE_MAX_OUTPUT_TOKENS",
                options.MaxOutputTokens);
        });
        services.AddHttpClient<IConversationTitleGenerator, OpenAiTitleGenerator>((provider, client) =>
        {
            var options = provider.GetRequiredService<IOptions<TitleOptions>>().Value;
            var openAi = provider.GetRequiredService<IOptions<OpenAIOptions>>().Value;
            client.BaseAddress = new Uri(openAi.BaseUrl);
            client.Timeout = TimeSpan.FromSeconds(Math.Max(1, options.TimeoutSeconds + 2));
        });
        services.AddHostedService<ConversationTitleWorker>();
        services.AddOptions<WebPushOptions>().Configure(o =>
        {
            configuration.GetSection("WebPush").Bind(o);
            o.Subject = Read(configuration, "AEGIS_WEB_PUSH_SUBJECT", o.Subject);
            o.PublicKey = Read(configuration, "AEGIS_WEB_PUSH_PUBLIC_KEY", o.PublicKey);
            o.PrivateKey = Read(configuration, "AEGIS_WEB_PUSH_PRIVATE_KEY", o.PrivateKey);
            o.PollSeconds = ReadInt(configuration, "AEGIS_REMINDER_POLL_SECONDS", o.PollSeconds);
        }).Validate(o => o.PollSeconds is >= 1 and <= 60, "Reminder polling must be between 1 and 60 seconds.")
          .Validate(o => string.IsNullOrEmpty(o.Subject) && string.IsNullOrEmpty(o.PublicKey) && string.IsNullOrEmpty(o.PrivateKey) || o.IsConfigured,
              "Configure a valid WebPush subject and VAPID key pair, or leave all three empty.")
          .ValidateOnStart();
        var semantic = new MemorySemanticOptions();
        configuration.GetSection("MemorySemantic").Bind(semantic);
        semantic.Enabled = ReadBool(configuration, "AEGIS_MEMORY_SEMANTIC_ENABLED", semantic.Enabled);
        semantic.EmbeddingModel = Read(configuration, "AEGIS_MEMORY_EMBEDDING_MODEL", semantic.EmbeddingModel);
        semantic.EmbeddingDimensions = ReadInt(configuration, "AEGIS_MEMORY_EMBEDDING_DIMENSIONS", semantic.EmbeddingDimensions);
        semantic.EmbeddingBaseUrl = Read(configuration, "AEGIS_MEMORY_EMBEDDING_BASE_URL", semantic.EmbeddingBaseUrl);
        semantic.EmbeddingApiKey = ReadAny(configuration, semantic.EmbeddingApiKey, "AEGIS_MEMORY_EMBEDDING_API_KEY", "OPENAI_API_KEY");
        semantic.QdrantBaseUrl = Read(configuration, "AEGIS_MEMORY_QDRANT_URL", semantic.QdrantBaseUrl);
        semantic.CollectionName = Read(configuration, "AEGIS_MEMORY_QDRANT_COLLECTION", semantic.CollectionName);
        semantic.WorkerPollSeconds = ReadInt(configuration, "AEGIS_MEMORY_PROJECTION_POLL_SECONDS", semantic.WorkerPollSeconds);
        if (double.TryParse(configuration["AEGIS_MEMORY_SEMANTIC_SCORE_THRESHOLD"], System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var threshold)) semantic.SearchScoreThreshold = threshold;
        if (semantic.EmbeddingDimensions is < 1 or > 3072 || semantic.WorkerPollSeconds is < 1 or > 60 ||
            semantic.SearchScoreThreshold is < 0 or > 1 || !double.IsFinite(semantic.SearchScoreThreshold) ||
            !System.Text.RegularExpressions.Regex.IsMatch(semantic.CollectionName, "^[A-Za-z0-9_-]{1,100}$"))
            throw new InvalidOperationException("Invalid MemorySemantic configuration.");
        services.AddSingleton(semantic);
        services.AddHttpClient<IMemoryEmbeddingClient, OpenAiMemoryEmbeddingClient>(client =>
        {
            client.BaseAddress = new Uri(semantic.EmbeddingBaseUrl.TrimEnd('/') + "/");
            client.Timeout = TimeSpan.FromSeconds(30);
        }).RemoveAllLoggers();
        services.AddHttpClient<IMemoryVectorStore, QdrantMemoryVectorStore>(client =>
        {
            client.BaseAddress = new Uri(semantic.QdrantBaseUrl.TrimEnd('/') + "/");
            client.Timeout = TimeSpan.FromSeconds(20);
        }).RemoveAllLoggers();
        services.AddScoped<IMemoryStore, MemoryStore>();
        services.AddScoped<IMemorySemanticProjectionStore, MemorySemanticProjectionStore>();
        services.AddScoped<MemorySemanticProjectionProcessor>();
        services.AddHostedService<MemorySemanticProjectionWorker>();
        services.AddScoped<ReminderStore>();
        services.AddScoped<IReminderStore>(p => p.GetRequiredService<ReminderStore>());
        services.AddScoped<ReminderProcessor>();
        services.AddScoped<IReminderInteractionTokens, ReminderInteractionTokens>();
        services.AddHttpClient<IWebPushClient, WebPushService>(client => client.Timeout = TimeSpan.FromSeconds(15))
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false })
            .RemoveAllLoggers();
        services.AddHostedService<ReminderWorker>();

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
