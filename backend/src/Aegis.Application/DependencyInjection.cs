using Aegis.Application.Chat;
using Aegis.Application.Prompts;
using Aegis.Application.Runtime;
using Microsoft.Extensions.DependencyInjection;

namespace Aegis.Application;

public static class DependencyInjection
{
    public static IServiceCollection AddApplication(this IServiceCollection services)
    {
        services.AddScoped<IChatService, ChatService>();
        services.AddSingleton<IRuntimeContextProvider, RuntimeContextProvider>();
        services.AddSingleton<IPromptBuilder, PromptBuilder>();

        return services;
    }
}
