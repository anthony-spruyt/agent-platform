using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace AgentPlatform.Host.RunData;

// Used by `dotnet ef` only. It never connects: migrations are scaffolded and compared offline.
internal sealed class RunDataDesignTimeFactory : IDesignTimeDbContextFactory<RunDataDbContext>
{
    public RunDataDbContext CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<RunDataDbContext>().UseNpgsql("Host=localhost;Database=agent_platform").Options);
}
