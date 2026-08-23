namespace WorkspaceRuntime.Application;

// A time-boxed consent that a session's screen content may be sent to a CLOUD
// vision model — the "screenshot leaves the machine" gate. Without it, the desktop
// brain stays AT-SPI-only whenever the resolved vision provider is cloud, so
// nothing leaves the box by default. On-box / remote-self-hosted vision never
// needs this. In-memory by design: a restart drops all consent (fail closed).
public sealed record VisionConsent(Guid Id, string SessionId, Guid GrantedByUserId, DateTimeOffset ExpiresAt);

public interface ISessionVisionConsent
{
    VisionConsent Grant(string sessionId, Guid grantedByUserId, TimeSpan duration, DateTimeOffset now);
    bool IsAllowed(string sessionId, DateTimeOffset now);
    int Revoke(string sessionId);
}

public sealed class InMemorySessionVisionConsent : ISessionVisionConsent
{
    private readonly object gate = new();
    private readonly List<VisionConsent> consents = new();

    public VisionConsent Grant(string sessionId, Guid grantedByUserId, TimeSpan duration, DateTimeOffset now)
    {
        var consent = new VisionConsent(Guid.NewGuid(), sessionId, grantedByUserId, now + duration);
        lock (gate)
        {
            consents.RemoveAll(existing => existing.SessionId == sessionId);
            consents.Add(consent);
        }
        return consent;
    }

    public bool IsAllowed(string sessionId, DateTimeOffset now)
    {
        lock (gate)
        {
            return consents.Any(consent => consent.SessionId == sessionId && consent.ExpiresAt > now);
        }
    }

    public int Revoke(string sessionId)
    {
        lock (gate)
        {
            return consents.RemoveAll(consent => consent.SessionId == sessionId);
        }
    }
}
