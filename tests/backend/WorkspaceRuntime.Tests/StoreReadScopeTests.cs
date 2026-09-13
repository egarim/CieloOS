using System.Text.RegularExpressions;

namespace WorkspaceRuntime.Tests;

// Store-wide reads are privileged: a route may return a runtime collection only
// after scoping it to the authenticated caller with Ownership.CanAccessHome.
public class StoreReadScopeTests
{
    [Fact]
    public void Store_wide_route_bodies_are_scoped_to_the_caller()
    {
        // EVERY file that maps routes, not just Program.cs. When the thread routes
        // were extracted into ThreadApi.cs this test kept passing while covering none
        // of them — a guard that silently stops guarding is worse than no guard,
        // because the green tick is read as assurance. Globbing the directory means
        // the next file of routes is covered the day it is written.
        var apiDirectory = Path.Combine(TestRepository.Root(), "src", "backend", "WorkspaceRuntime.Api");
        var source = string.Join("\n", Directory
            .EnumerateFiles(apiDirectory, "*.cs", SearchOption.AllDirectories)
            .Where(file => !file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && !file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .OrderBy(file => file, StringComparer.Ordinal)
            .Select(File.ReadAllText));

        var routes = ReadRoutes(source);
        Assert.NotEmpty(routes);
        // The thread routes live outside Program.cs; if the glob ever stops reaching
        // them this fails here rather than passing with nothing to check.
        Assert.Contains(routes, route => route.Path.StartsWith("/api/threads", StringComparison.Ordinal));

        var offenders = routes
            .Where(route => IsStoreWideRead(route.Body)
                && (!route.Body.Contains("HttpContext context", StringComparison.Ordinal)
                    || !route.Body.Contains("Ownership.CanAccessHome", StringComparison.Ordinal)))
            .Select(route => $"{route.Method} {route.Path}")
            .ToList();

        Assert.True(offenders.Count == 0,
            "Store-wide route reads must be scoped to the caller. Offenders:\n" + string.Join("\n", offenders));
    }

    private sealed record RouteRegistration(string Method, string Path, string Body);

    private static IReadOnlyList<RouteRegistration> ReadRoutes(string source)
    {
        var registrations = Regex.Matches(source, @"app\.Map(?:Get|Post|Put|Delete)\(\s*""(?<path>[^""]+)""");
        return registrations
            .Select(match =>
            {
                var method = match.Value.Contains("MapDelete", StringComparison.Ordinal) ? "DELETE" : match.Value.Contains("MapPut", StringComparison.Ordinal) ? "PUT" : match.Value.Contains("MapPost", StringComparison.Ordinal) ? "POST" : "GET";
                var start = match.Index;
                var stop = new[] { "\napp.Map", "\nstatic ", "\npublic " }
                    .Select(marker => source.IndexOf(marker, start, StringComparison.Ordinal))
                    .Where(index => index >= 0)
                    .Append(source.Length)
                    .Min();
                // Arguments were swapped against the record's (Method, Path, Body)
                // order, so Path held the verb. Nothing read Path until now, which is
                // why it went unnoticed — the offender message just printed backwards.
                return new RouteRegistration(method, match.Groups["path"].Value, source[start..stop]);
            })
            .ToList();
    }

    private static bool IsStoreWideRead(string body) =>
        Regex.IsMatch(body, @"(?:=>|return)\s*store\.(?:Workspaces|Agents|AuditEvents|Approvals|Spreadsheet|Threads)\b")
        || Regex.IsMatch(body, @"foreach\s*\([^)]*\bin\s+store\.(?:Workspaces|Agents|AuditEvents|Approvals|Spreadsheet|Threads)\b")
        // Results.Ok(store.X) is the shape ThreadApi actually uses. Matching only
        // "return store.X" meant an unscoped Results.Ok(store.Threads) sailed straight
        // past — the guard read the right file and still could not fail.
        || Regex.IsMatch(body, @"Results\.Ok\(\s*store\.(?:Workspaces|Agents|AuditEvents|Approvals|Spreadsheet|Threads)\b")
        || Regex.IsMatch(body, @"store\.GetThread\(");
}
