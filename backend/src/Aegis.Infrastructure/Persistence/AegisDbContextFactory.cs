using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Aegis.Infrastructure.Persistence;

public sealed class AegisDbContextFactory : IDesignTimeDbContextFactory<AegisDbContext>
{
    private const string DefaultConnectionString =
        "Host=localhost;Port=5432;Database=aegis;Username=aegis;Password=aegis_dev_password";

    public AegisDbContext CreateDbContext(string[] args)
    {
        var connectionString = Environment.GetEnvironmentVariable("ConnectionStrings__AegisDatabase")
            ?? Environment.GetEnvironmentVariable("AEGIS_DB_CONNECTION_STRING")
            ?? DefaultConnectionString;

        var options = new DbContextOptionsBuilder<AegisDbContext>()
            .UseNpgsql(connectionString)
            .Options;

        return new AegisDbContext(options);
    }
}
