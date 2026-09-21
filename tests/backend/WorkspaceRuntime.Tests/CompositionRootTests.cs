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
public class CompositionRootTests : IClassFixture<CompositionRootTests.Host>
{
    private readonly Host host;

    public CompositionRootTests(Host host) => this.host = host;

    // The direct test of the defect. Enumerating the endpoints is exactly what the
    // routing middleware does on first request, so this throws for the same reason
    // and with the same message the box produced: "Failure to infer one or more
    // parameters ... invites | UNKNOWN".
    [Fact]
    public void Every_endpoint_can_resolve_its_services()
    {
        var endpoints = host.Services.GetRequiredService<EndpointDataSource>().Endpoints;
        Assert.NotEmpty(endpoints);
    }

    // And the symptom, from the outside, over HTTP. This is the request the panel
    // makes before it renders anything and the one cielo-claim polls after install,
    // so it is the single request whose failure means "the machine is dead".
    [Fact]
    public async Task Setup_status_answers()
    {
        using var client = host.CreateClient();

        var response = await client.GetAsync("/api/setup/status");

        Assert.True(
            response.IsSuccessStatusCode,
            $"/api/setup/status returned {(int)response.StatusCode}. "
            + "On a real install this is the first request the panel makes, so anything "
            + "but a success here is a box that never comes up.");
    }

    public sealed class Host : WebApplicationFactory<Program>
    {
        protected override IHost CreateHost(IHostBuilder builder)
        {
            // The memory provider so the test needs no database, no /var/lib/cielo and
            // no migration. It is a real configuration of the real Program: both
            // branches of every store registration are reachable this way, and a
            // service missing from BOTH — which is what happened — fails either way.
            builder.UseEnvironment("Development");
            builder.ConfigureHostConfiguration(config => config.AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["Database:Provider"] = "memory",
                    ["Runtime:SeedDemo"] = "false"
                }));
            return base.CreateHost(builder);
        }
    }
}
