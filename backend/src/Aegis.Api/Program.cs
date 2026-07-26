using Aegis.Application;
using Aegis.Infrastructure;
using Aegis.Infrastructure.Persistence;
using Aegis.Application.Voice;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

const string CorsPolicyName = "AegisCors";

builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
builder.Services.AddCors(options =>
{
    options.AddPolicy(CorsPolicyName, policy =>
    {
        policy
            .AllowAnyHeader()
            .AllowAnyMethod()
            .WithOrigins(
                "http://localhost:5173",
                "https://localhost:5173",
                "http://localhost:3000",
                "https://localhost:3000");
    });
});

builder.Services
    .AddApplication()
    .AddInfrastructure(builder.Configuration);

var app = builder.Build();

app.Lifetime.ApplicationStopping.Register(() =>
{
    using var scope = app.Services.CreateScope();
    var voice = scope.ServiceProvider.GetRequiredService<IVoiceService>();
    voice.CancelAllTurnsAsync("application_shutdown").GetAwaiter().GetResult();
});

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();

    await ApplyDevelopmentMigrationsAsync(app.Services, app.Logger);
}

app.UseCors(CorsPolicyName);

app.UseAuthorization();

app.MapControllers();

app.Run();

static async Task ApplyDevelopmentMigrationsAsync(IServiceProvider services, ILogger logger)
{
    const int maxAttempts = 10;

    for (var attempt = 1; attempt <= maxAttempts; attempt++)
    {
        try
        {
            using var scope = services.CreateScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<AegisDbContext>();
            await dbContext.Database.MigrateAsync();
            return;
        }
        catch (Exception exception) when (attempt < maxAttempts)
        {
            logger.LogWarning(
                exception,
                "Database migration failed on attempt {Attempt}/{MaxAttempts}. Retrying shortly.",
                attempt,
                maxAttempts);

            await Task.Delay(TimeSpan.FromSeconds(2));
        }
    }
}
