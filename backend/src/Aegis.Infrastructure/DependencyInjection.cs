using Aegis.Application.Common;
using Aegis.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

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

        return services;
    }
}
