using Aegis.Application.Nodes;
using Aegis.Infrastructure.Nodes;
using Aegis.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

// This executable has no HTTP listener. Access requires local server/DB administration.
if (args.Length == 0 || args[0] is not ("pairing-code" or "enable" or "cleanup" or "migrate"))
{ Console.Error.WriteLine("Usage: Aegis.NodeAdmin pairing-code | enable <NodeId> | cleanup | migrate"); return 2; }
var connection = Environment.GetEnvironmentVariable("ConnectionStrings__AegisDatabase") ?? Environment.GetEnvironmentVariable("AEGIS_DB_CONNECTION_STRING");
if (string.IsNullOrWhiteSpace(connection)) { Console.Error.WriteLine("Configure the local administrative database connection."); return 2; }
await using var db = new AegisDbContext(new DbContextOptionsBuilder<AegisDbContext>().UseNpgsql(connection).Options);
var registry = new NodeRegistry(db, TimeProvider.System);
try
{
    switch (args[0])
    {
        case "migrate": await db.Database.MigrateAsync(); Console.WriteLine("Database migrations applied."); break;
        case "pairing-code":
            var code = await registry.CreateCodeAsync(null);
            Console.WriteLine($"Pairing code: {code.Code}\nExpires (UTC): {code.ExpiresAt:O}\nUse once on the intended device; do not share in logs or tickets."); break;
        case "enable" when args.Length == 2 && Guid.TryParse(args[1], out var id):
            await registry.SetEnabledAsync(null, id, true); Console.WriteLine("Node enabled."); break;
        case "cleanup": await registry.CleanupAsync(); Console.WriteLine("Expired pairing material removed."); break;
        default: Console.Error.WriteLine("Invalid arguments."); return 2;
    }
    return 0;
}
catch (NodeException error) { Console.Error.WriteLine(error.Message); return 1; }
catch { Console.Error.WriteLine("Local Node administration failed. Check database reachability and migration state."); return 1; }
