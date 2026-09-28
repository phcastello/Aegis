using Aegis.Application.Reminders;
using Aegis.Application.Memory;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Aegis.Application.Chat;
using Aegis.Application.Calendar;
using Aegis.Application.Calendar.Tools;
using Aegis.Application.Email;
using Aegis.Application.Email.Tools;
using Aegis.Application.Feedback;
using Aegis.Application.Models;
using Aegis.Application.Prompts;
using Aegis.Application.Runtime;
using Aegis.Application.Tools;
using Aegis.Application.Turns;
using Aegis.Application.Voice;
using Aegis.Application.Voice.Transcription;
using Aegis.Application.Observability;
using Microsoft.Extensions.DependencyInjection;

namespace Aegis.Application;

public static class DependencyInjection
{
    public static IServiceCollection AddApplication(this IServiceCollection services)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.AddScoped<ReminderService>();
        services.AddScoped<MemoryService>();
        services.AddScoped<MemorySemanticSearch>();
        services.AddScoped<MemoryEntityResolver>();
        services.AddScoped<MemoryGraphQuery>();
        services.AddScoped<IAegisTool, MemoryRememberTool>();
        services.AddScoped<IAegisTool, MemorySearchTool>();
        services.AddScoped<IAegisTool, MemoryUpdateTool>();
        services.AddScoped<IAegisTool, MemoryForgetTool>();
        services.AddScoped<IAegisTool, ReminderCreateTool>();
        services.AddScoped<IAegisTool, ReminderListTool>();
        services.AddScoped<IAegisTool, ReminderUpdateTool>();
        services.AddScoped<IAegisTool, ReminderCancelTool>();
        services.AddScoped<IChatService, ChatService>();
        services.AddSingleton<AegisMetrics>();
        services.AddScoped<IVoiceService, VoiceService>();
        services.AddScoped<ISpeechTranscriptionService, SpeechTranscriptionService>();
        services.AddSingleton<VoiceStatusCache>();
        services.AddSingleton<IActiveTurnRegistry, ActiveTurnRegistry>();
        services.AddScoped<IConversationTitleService, ConversationTitleService>();
        services.AddScoped<IMessageFeedbackService, MessageFeedbackService>();
        services.AddScoped<CalendarToolContextService>();
        services.AddScoped<IAegisTool, CalendarGetStatusTool>();
        services.AddScoped<IAegisTool, CalendarCreateConnectLinkTool>();
        services.AddScoped<IAegisTool, CalendarListCalendarsTool>();
        services.AddScoped<IAegisTool, CalendarListEventsTool>();
        services.AddScoped<IAegisTool, CalendarGetEventTool>();
        services.AddScoped<IAegisTool, CalendarCreateEventTool>();
        services.AddScoped<IAegisTool, CalendarUpdateEventTool>();
        services.AddScoped<IAegisTool, CalendarDeleteEventTool>();
        services.AddScoped<IAegisTool, CalendarAmendPendingActionTool>();
        services.AddScoped<IAegisTool, CalendarConfirmPendingActionTool>();
        services.AddScoped<IAegisTool, CalendarCancelPendingActionTool>();
        services.AddScoped<IEmailToolContextService, EmailToolContextService>();
        services.AddScoped<IAegisToolLoop, AegisToolLoop>();
        services.AddScoped<IAegisToolRegistry, AegisToolRegistry>();
        services.AddScoped<IAegisTool, EmailGetStatusTool>();
        services.AddScoped<IAegisTool, EmailCreateConnectLinkTool>();
        services.AddScoped<IAegisTool, EmailSearchTool>();
        services.AddScoped<IAegisTool, EmailReadTool>();
        services.AddScoped<IAegisTool, EmailReadThreadTool>();
        services.AddScoped<IAegisTool, EmailMarkReadTool>();
        services.AddScoped<IAegisTool, EmailMarkUnreadTool>();
        services.AddScoped<IAegisTool, EmailStarTool>();
        services.AddScoped<IAegisTool, EmailUnstarTool>();
        services.AddScoped<IAegisTool, EmailMarkImportantTool>();
        services.AddScoped<IAegisTool, EmailUnmarkImportantTool>();
        services.AddScoped<IAegisTool, EmailConfirmPendingActionTool>();
        services.AddScoped<IAegisTool, EmailCancelPendingActionTool>();
        services.AddSingleton<IConversationTitleJobQueue, ConversationTitleJobQueue>();
        services.AddSingleton<IRuntimeContextProvider, RuntimeContextProvider>();
        services.AddSingleton<IPromptBuilder, PromptBuilder>();

        return services;
    }
}
