using Aegis.Application.Chat;
using Aegis.Application.Feedback;
using Aegis.Application.Prompts;
using Aegis.Application.Runtime;
using Microsoft.Extensions.DependencyInjection;

namespace Aegis.Application;

public static class DependencyInjection
{
    public static IServiceCollection AddApplication(this IServiceCollection services)
    {
        services.AddScoped<IChatService, ChatService>();
        services.AddScoped<IConversationTitleService, ConversationTitleService>();
        services.AddScoped<IMessageFeedbackService, MessageFeedbackService>();
        services.AddSingleton<IConversationTitleJobQueue, ConversationTitleJobQueue>();
        services.AddSingleton<IRuntimeContextProvider, RuntimeContextProvider>();
        services.AddSingleton<IPromptBuilder, PromptBuilder>();

        return services;
    }
}
