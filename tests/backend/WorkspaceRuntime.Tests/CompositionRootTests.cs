using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace WorkspaceRuntime.Tests;

// Every other test in this suite builds its own services. That is what makes them
// fast and focused, and it is also why 581 of them passed while the installed
// product answered 500 to every request: IInviteStore had stores, routes, and
// coverage, and no registration in Program.cs.
//
// The failure mode is worth stating, because it is not "the invite routes break".
// Minimal APIs infer each endpoint's parameters when the endpoint data source is
// first enumerated, an unregistered service type throws there, and that enumeration
// happens inside EndpointRoutingMiddleware on the first request of ANY kind. One
// missing line took down /api/setup/status, the panel, and the whole box.
//
// So these tests start the REAL composition root — the one systemd starts — and
// assert things that can only be true of it.
public abstract class CompositionRootTests
{
    protected abstract CompositionHost Host { get; }

    // The direct test of the defect. Enumerating the endpoints is exactly what the
    // routing middleware does on first request, so this throws for the same reason
    // and with the same message the box produced: "Failure to infer one or more
    // parameters ... invites | UNKNOWN".
    [Fact]
    public void Every_endpoint_can_resolve_its_services()
    {
        var endpoints = Host.Services.GetRequiredService<EndpointDataSource>().Endpoints;
        Assert.NotEmpty(endpoints);
    }

    // And the symptom, from the outside, over HTTP. This is the request the panel
    // makes before it renders anything and the one the install-time claim polls, so
    // it is the single request whose failure means "the machine is dead". It also
    // walks the auth middleware, which resolves several services by hand out of
    // RequestServices rather than as endpoint parameters — those cannot be caught by
    // enumerating endpoints, and are covered only because something makes a request.
    [Fact]
    public async Task Setup_status_answers()
    {
        using var client = Host.CreateClient();

        var response = await client.GetAsync("/api/setup/status");

        Assert.True(
            response.IsSuccessStatusCode,
            $"/api/setup/status returned {(int)response.StatusCode}. "
            + "On a real install this is the first request the panel makes, so anything "
            + "but a success here is a box that never comes up.");
    }

    public abstract class CompositionHost : WebApplicationFactory<Program>
    {
        protected abstract IEnumerable<KeyValuePair<string, string?>> Settings { get; }

        protected override IHost CreateHost(IHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.ConfigureHostConfiguration(config => config.AddInMemoryCollection(
                Settings.Append(new KeyValuePair<string, string?>("Runtime:SeedDemo", "false"))));
            return base.CreateHost(builder);
        }
    }
}

// The provider a developer runs and the fast one. Needs no database, no
// /var/lib/cielo and no migration.
public sealed class MemoryCompositionRootTests
    : CompositionRootTests, IClassFixture<MemoryCompositionRootTests.MemoryHost>
{
    public MemoryCompositionRootTests(MemoryHost host) => Host = host;

    protected override CompositionHost Host { get; }

    public sealed class MemoryHost : CompositionHost
    {
        protected override IEnumerable<KeyValuePair<string, string?>> Settings =>
            [new("Database:Provider", "memory")];
    }
}

// The provider install.sh actually configures. Running only the memory one would
// leave a service registered in a single branch of that switch undetectable — the
// bug that prompted these tests was missing from both branches and so would have
// been caught either way, but the next one need not be.
public sealed class SqliteCompositionRootTests
    : CompositionRootTests, IClassFixture<SqliteCompositionRootTests.SqliteHost>
{
    public SqliteCompositionRootTests(SqliteHost host) => Host = host;

    protected override CompositionHost Host { get; }

    public sealed class SqliteHost : CompositionHost
    {
        // A file, not :memory:, because EF opens and closes connections per context
        // and an in-memory SQLite database dies with the first one.
        private readonly string databasePath =
            Path.Combine(Path.GetTempPath(), $"cielo-composition-{Guid.NewGuid():N}.db");

        protected override IEnumerable<KeyValuePair<string, string?>> Settings =>
        [
            new("Database:Provider", "sqlite"),
            new("Database:SqlitePath", databasePath),
            // Migrations are the installed box's job; this test is about whether the
            // services resolve, so let EF create the schema and keep it hermetic.
            new("Database:EnsureCreated", "true")
        ];

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (!disposing) return;
            try { File.Delete(databasePath); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }
}
