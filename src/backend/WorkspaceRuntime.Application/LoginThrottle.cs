using System.Collections.Concurrent;

namespace WorkspaceRuntime.Application;

// Failed sign-ins, counted per desk and per source address.
//
// Verifying a password costs a deliberate ~100 ms of PBKDF2. That is the point
// against an offline attack, and a liability against an online one: on a headless
// install the login endpoint is public, so an attacker gets both a guessing
// oracle and a way to spend the machine's CPU by asking repeatedly.
//
// Deliberately in memory: a restart forgiving the counters is the right trade for
// a single box, and it keeps a failed login off the write path of the database.
public sealed class LoginThrottle
{
    private readonly ConcurrentDictionary<string, Attempts> attempts = new(StringComparer.OrdinalIgnoreCase);

    private readonly int limit;
    private readonly TimeSpan window;

    public LoginThrottle(int limit = 8, TimeSpan? window = null)
    {
        this.limit = limit;
        this.window = window ?? TimeSpan.FromMinutes(15);
    }

    // Returns how long the caller must wait, or null if it may try now. Checked
    // BEFORE the password is verified, so a blocked attempt costs no PBKDF2.
    public TimeSpan? RetryAfter(string key, DateTimeOffset now)
    {
        if (!attempts.TryGetValue(key, out var record))
        {
            return null;
        }

        if (now - record.FirstFailure > window)
        {
            attempts.TryRemove(key, out _);
            return null;
        }

        return record.Count >= limit ? window - (now - record.FirstFailure) : null;
    }

    public void Failed(string key, DateTimeOffset now) =>
        attempts.AddOrUpdate(
            key,
            _ => new Attempts(1, now),
            (_, existing) => now - existing.FirstFailure > window
                ? new Attempts(1, now)
                : existing with { Count = existing.Count + 1 });

    // A success clears the count: the person proved they are who they said, and
    // a fat-fingered password earlier should not follow them around.
    public void Succeeded(string key) => attempts.TryRemove(key, out _);

    private sealed record Attempts(int Count, DateTimeOffset FirstFailure);
}
