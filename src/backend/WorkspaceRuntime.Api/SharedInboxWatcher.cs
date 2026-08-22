using WorkspaceRuntime.Application;
using WorkspaceRuntime.Domain;
using WorkspaceRuntime.Infrastructure;

namespace WorkspaceRuntime.Api;

// Turns a message dropped into a shared workspace's inbox.md (e.g. from the
// desktop "Message Agent" launcher) into an agent reply: it watches each owner's
// lunos-shared-<user>/inbox.md, and for every new line runs the console loop as
// that owner-through-their-agent, which writes the reply to ~/shared/outbox.md.
// The desktop stays credential-free (design law 7) — the runtime does the
// authenticated dispatch.
public sealed class SharedInboxWatcher : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(3);

    private readonly IRuntimeStore store;
    private readonly ISessionBackend sessions;
    private readonly IHomeBrowser home;
    private readonly ConsoleAgentLoop loop;
    private readonly IConsoleBrainRegistry brains;
    private readonly ILogger<SharedInboxWatcher> logger;

    // Per-user count of inbox lines already handled — baselined at startup so we
    // never reprocess history, in-memory so a restart re-baselines rather than
    // replaying old messages.
    private readonly Dictionary<string, int> handled = new(StringComparer.Ordinal);

    public SharedInboxWatcher(
        IRuntimeStore store, ISessionBackend sessions, IHomeBrowser home,
        ConsoleAgentLoop loop, IConsoleBrainRegistry brains, ILogger<SharedInboxWatcher> logger)
    {
        this.store = store;
        this.sessions = sessions;
        this.home = home;
        this.loop = loop;
        this.brains = brains;
        this.logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        foreach (var user in store.Users)
        {
            handled[user.Slug] = (await InboxLinesAsync(user.Slug, stoppingToken)).Count;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await PollAsync(stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "Shared inbox poll failed.");
            }

            try
            {
                await Task.Delay(PollInterval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task PollAsync(CancellationToken cancellationToken)
    {
        foreach (var user in store.Users)
        {
            var lines = await InboxLinesAsync(user.Slug, cancellationToken);
            var already = handled.GetValueOrDefault(user.Slug, 0);
            handled[user.Slug] = lines.Count;
            if (lines.Count <= already)
            {
                continue;
            }

            foreach (var line in lines.Skip(already))
            {
                var message = Message(line);
                if (!string.IsNullOrWhiteSpace(message))
                {
                    await DispatchAsync(user, message, cancellationToken);
                }
            }
        }
    }

    private async Task<IReadOnlyList<string>> InboxLinesAsync(string userSlug, CancellationToken cancellationToken)
    {
        var file = await home.ReadSharedAsync(userSlug, "inbox.md", cancellationToken);
        return file is null
            ? Array.Empty<string>()
            : file.Content.Replace("\r\n", "\n").Split('\n');
    }

    // Lines are "<iso-timestamp>\t<message>"; tolerate a bare message too.
    private static string Message(string line)
    {
        var tab = line.IndexOf('\t');
        return (tab >= 0 ? line[(tab + 1)..] : line).Trim();
    }

    private async Task DispatchAsync(PlatformUser user, string message, CancellationToken cancellationToken)
    {
        var agent = store.Agents.FirstOrDefault(candidate => candidate.OwnerUserId == user.Id);
        if (agent is null)
        {
            return;
        }

        var session = (await sessions.ListAsync(cancellationToken))
            .FirstOrDefault(s => string.Equals(s.Owner, agent.Slug, StringComparison.Ordinal)
                && s.Kind == "console" && s.Status == "running");
        if (session is null)
        {
            logger.LogInformation("Inbox message for {User} but {Agent} has no running console session.", user.Slug, agent.Slug);
            return;
        }

        var principal = new RuntimePrincipal(PrincipalKind.Human, user.Id, user.Slug, user.DisplayName);
        var goal =
            $"Your owner sent you this message from the desktop: \"{message}\". Respond helpfully — if it " +
            "asks you to do something, use your tools (websearch, python3, the files in ~ and ~/shared). When " +
            "finished, write a short reply to your owner by overwriting ~/shared/outbox.md, then you are done.";

        logger.LogInformation("Dispatching inbox message from {User} to {Agent}.", user.Slug, agent.Slug);
        var brain = brains.Resolve(agent.InferenceProvider).Brain;
        await loop.RunAsync(session.Id, goal, 8, principal, user.Id, agent.Id, brain, cancellationToken);
    }
}
