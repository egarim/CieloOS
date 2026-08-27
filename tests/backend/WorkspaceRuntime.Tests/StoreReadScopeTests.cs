using System.Text.RegularExpressions;

namespace WorkspaceRuntime.Tests;

// Store-wide reads are privileged: a route may return a runtime collection only
// after scoping it to the authenticated caller with Ownership.CanAccessHome.
public class StoreReadScopeTests
{
    [Fact]
    public void Store_wide_route_bodies_are_scoped_to_the_caller()
    {
        var source = File.ReadAllText(Path.Combine(TestRepository.Root(), "src", "backend", "WorkspaceRuntime.Api", "Program.cs"));
        var routes = ReadRoutes(source);
        Assert.NotEmpty(routes);

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
                return new RouteRegistration(match.Groups["path"].Value, method, source[start..stop]);
            })
            .ToList();
    }

    private static bool IsStoreWideRead(string body) =>
        Regex.IsMatch(body, @"(?:=>|return)\s*store\.(?:Workspaces|Agents|AuditEvents|Approvals|Spreadsheet)\b")
        || Regex.IsMatch(body, @"foreach\s*\([^)]*\bin\s+store\.(?:Workspaces|Agents|AuditEvents|Approvals|Spreadsheet)\b")
        || Regex.IsMatch(body, @"Results\.Ok\(\s*store\.Spreadsheet\b");
}
