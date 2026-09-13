using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using WorkspaceRuntime.Application;
using WorkspaceRuntime.Domain;
using WorkspaceRuntime.Infrastructure;

namespace WorkspaceRuntime.Tests;

public sealed class ThreadPersistenceTests : IDisposable
{
    private readonly string databasePath = Path.Combine(Path.GetTempPath(), $"workspace-runtime-threads-{Guid.NewGuid():N}.db");

    private EfRuntimeStore CreateStore()
    {
        var options = new DbContextOptionsBuilder<RuntimeDbContext>()
            .UseSqlite($"Data Source={databasePath}")
            .Options;
        return new EfRuntimeStore(new PooledDbContextFactory<RuntimeDbContext>(options), ensureCreated: true);
    }

    [Fact]
    public void Thread_and_messages_survive_a_restart()
    {
        var store = CreateStore();
        var owner = store.Users[0].Slug;
        var thread = store.CreateThread(owner, "September invoice", "Please get the September invoice.");
        store.AppendThreadMessage(thread.Id, ThreadMessageRole.Agent, "I am on it.");
        store.SetThreadState(thread.Id, ThreadStatus.NeedsYou);

        var reopened = CreateStore();
        var detail = reopened.GetThread(thread.Id);

        Assert.NotNull(detail);
        Assert.Equal(owner, detail.Thread.OwnerSlug);
        Assert.Equal("September invoice", detail.Thread.Title);
        Assert.Equal(ThreadStatus.NeedsYou, detail.Thread.State);
        Assert.Equal(2, detail.Messages.Count);
        Assert.Equal("Please get the September invoice.", detail.Messages[0].Text);
        Assert.Equal(ThreadMessageRole.Person, detail.Messages[0].Role);
        Assert.Equal("I am on it.", detail.Messages[1].Text);
        Assert.Equal(ThreadMessageRole.Agent, detail.Messages[1].Role);
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (File.Exists(databasePath))
        {
            File.Delete(databasePath);
        }
    }
}

public class ThreadOwnershipTests
{
    [Fact]
    public async Task A_user_cannot_read_another_users_threads()
    {
        var store = new InMemoryRuntimeStore();
        var joche = store.Users.First(user => user.Slug == "joche");
        var yulia = store.Users.First(user => user.Slug == "yulia");

        var jocheThread = store.CreateThread(joche.Slug, "Joche private work", "First message");
        var yuliaThread = store.CreateThread(yulia.Slug, "Yulia private work", "First message");

        await using var api = await StartThreadsApiAsync(store, slug => slug == "joche" ? Principal(joche) : slug == "yulia" ? Principal(yulia) : null);
        using var response = await GetAsync(api.Client, "/api/threads", "joche");

        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var ids = document.RootElement.EnumerateArray().Select(ReadId).ToList();

        Assert.Contains(jocheThread.Id, ids);
        Assert.DoesNotContain(yuliaThread.Id, ids);
    }

    [Fact]
    public async Task Another_users_thread_id_is_indistinguishable_from_a_missing_thread()
    {
        var store = new InMemoryRuntimeStore();
        var joche = store.Users.First(user => user.Slug == "joche");
        var yulia = store.Users.First(user => user.Slug == "yulia");

        var yuliaThread = store.CreateThread(yulia.Slug, "Yulia private work", "First message");

        var missingId = Guid.NewGuid();
        await using var api = await StartThreadsApiAsync(store, slug => slug == "joche" ? Principal(joche) : null);
        using var foreign = await GetAsync(api.Client, $"/api/threads/{yuliaThread.Id}", "joche");
        using var missing = await GetAsync(api.Client, $"/api/threads/{missingId}", "joche");

        Assert.Equal(HttpStatusCode.NotFound, foreign.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);

        // Byte-for-byte identical, with no normalisation. An earlier version of this
        // test substituted the ids out before comparing, which let the endpoint echo
        // the id and still "pass" — an assertion relaxed to fit the implementation
        // rather than to state the requirement. The requirement is that the two
        // answers are indistinguishable, so the test says exactly that.
        var foreignBody = await foreign.Content.ReadAsStringAsync();
        var missingBody = await missing.Content.ReadAsStringAsync();
        Assert.Equal(missingBody, foreignBody);
        Assert.DoesNotContain(yuliaThread.Id.ToString(), foreignBody, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(yulia.Slug, foreignBody, StringComparison.OrdinalIgnoreCase);
    }

    private static RuntimePrincipal Principal(PlatformUser user) =>
        new(PrincipalKind.Human, user.Id, user.Slug, user.DisplayName);

    private static async Task<HttpResponseMessage> GetAsync(HttpClient client, string path, string principalSlug)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Add("X-Test-Principal", principalSlug);
        return await client.SendAsync(request);
    }

    private static Guid ReadId(JsonElement element) =>
        element.EnumerateObject()
            .Single(property => property.Name.Equals("id", StringComparison.OrdinalIgnoreCase))
            .Value.GetGuid();

    private static async Task<TestThreadApi> StartThreadsApiAsync(IRuntimeStore store, Func<string, RuntimePrincipal?> resolvePrincipal)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddSingleton<IRuntimeStore>(store);
        var app = builder.Build();

        app.Use(async (context, next) =>
        {
            var slug = context.Request.Headers["X-Test-Principal"].ToString();
            if (resolvePrincipal(slug) is { } principal)
            {
                context.Items["principal"] = principal;
            }
            await next();
        });

        ThreadApi.Map(app);
        await app.StartAsync();

        var address = app.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()!
            .Addresses.First();
        return new TestThreadApi(app, new HttpClient { BaseAddress = new Uri(address) });
    }

    private sealed class TestThreadApi : IAsyncDisposable
    {
        public TestThreadApi(WebApplication app, HttpClient client)
        {
            App = app;
            Client = client;
        }

        public WebApplication App { get; }
        public HttpClient Client { get; }

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await App.DisposeAsync();
        }
    }
}