using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace WorkspaceRuntime.Infrastructure;

// Used only by `dotnet ef` at design time; the runtime provider comes from configuration.
public sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<RuntimeDbContext>
{
    public RuntimeDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<RuntimeDbContext>()
            .UseSqlite("Data Source=design-time.db")
            .Options;
        return new RuntimeDbContext(options);
    }
}
