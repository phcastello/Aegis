using System.Reflection;
using Aegis.Api.Controllers;
using Aegis.Api.Nodes;
using Aegis.Application.Nodes;
using Aegis.Infrastructure.Nodes;
using Aegis.Infrastructure.Persistence;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.EntityFrameworkCore;

// CI fixture only: loopback listener, disposable in-memory DB, local one-time bootstrap output.
// This project is not referenced by the solution or published API/NodeAdmin image.
if (args.Length != 1) return 2;
var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = [], EnvironmentName = "Development" });
builder.WebHost.UseUrls("http://127.0.0.1:18104");
builder.Logging.SetMinimumLevel(LogLevel.Warning);
builder.Services.AddControllers().AddApplicationPart(typeof(NodesController).Assembly)
    .ConfigureApplicationPartManager(manager => { manager.FeatureProviders.Clear(); manager.FeatureProviders.Add(new NodeControllersOnly()); });
builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?> {
    ["NodeTransport:HeartbeatSeconds"] = "1", ["NodeTransport:TimeoutSeconds"] = "3",
    ["NodeTransport:MinimumHeartbeatSeconds"] = "1", ["NodeTransport:WatchdogSeconds"] = "1", ["NodeTransport:PersistSeconds"] = "2" });
builder.Services.AddNodeApi(builder.Configuration);
builder.Services.AddScoped<INodeTransportHistory, NodeTransportHistory>(); builder.Services.AddSingleton(TimeProvider.System);
var database = "native-ci-" + Guid.NewGuid();
builder.Services.AddDbContext<AegisDbContext>(o => o.UseInMemoryDatabase(database));
builder.Services.AddScoped<INodeRegistry, NodeRegistry>();
var app = builder.Build(); app.UseWebSockets(); app.UseAuthentication(); app.UseAuthorization(); app.UseRateLimiter(); app.MapControllers();
app.MapGet("/api/health", () => new { status = "ok" });
using (var scope = app.Services.CreateScope())
{
    var code = await scope.ServiceProvider.GetRequiredService<INodeRegistry>().CreateCodeAsync(null);
    await File.WriteAllTextAsync(args[0], code.Code); // Temporary proof only, never a Node credential.
}
await app.RunAsync(); return 0;
sealed class NodeControllersOnly : ControllerFeatureProvider
{
    protected override bool IsController(TypeInfo type) => type.AsType() == typeof(NodesController) || type.AsType() == typeof(NodePairingController) || type.AsType() == typeof(NodeTransportController);
}
