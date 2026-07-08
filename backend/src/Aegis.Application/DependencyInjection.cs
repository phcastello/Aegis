using Aegis.Application.Chat;
using Aegis.Application.Email;
using Aegis.Application.Email.Tools;
using Aegis.Application.Feedback;
using Aegis.Application.Models;
using Aegis.Application.Prompts;
using Aegis.Application.Runtime;
using Aegis.Application.Tools;
using Microsoft.Extensions.DependencyInjection;

namespace Aegis.Application;

public static class DependencyInjection
{
    public static IServiceCollection AddApplication(this IServiceCollection services)
    {
        services.AddScoped<IChatService, ChatService>();
        services.AddScoped<IConversationTitleService, ConversationTitleService>();
        services.AddScoped<IMessageFeedbackService, MessageFeedbackService>();
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
        services.AddSingleton<AegisModelRouter>();
        services.AddSingleton<IConversationTitleJobQueue, ConversationTitleJobQueue>();
        services.AddSingleton<IRuntimeContextProvider, RuntimeContextProvider>();
        services.AddSingleton<IPromptBuilder, PromptBuilder>();

        return services;
    }
}
