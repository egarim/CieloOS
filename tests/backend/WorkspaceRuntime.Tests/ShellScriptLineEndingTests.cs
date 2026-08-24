namespace WorkspaceRuntime.Tests;

// A multi-line C# literal keeps the line endings of its source file. When that
// literal is a shell script, a checkout that uses CRLF ships `set -eu\r` to bash
// — a syntax error — and every home listing, preview and download silently
// returns nothing. It shipped exactly once, and nothing caught it: CI checks out
// LF, so the bug only exists in binaries built from a Windows working tree.
//
// This is a lint, in the shape of RenameSafetyTests: the script literal must be
// handed to a normaliser rather than passed to bash directly.
public class ShellScriptLineEndingTests
{
    [Fact]
    public void Shell_script_literals_are_normalised_before_they_reach_bash()
    {
        var root = FindRepositoryRoot();
        var source = File.ReadAllText(Path.Combine(
            root, "src", "backend", "WorkspaceRuntime.Infrastructure", "PodmanHomeBrowser.cs"));

        Assert.DoesNotContain("\"-c\", Guard", source);
        Assert.Contains("Replace(\"\\r\\n\", \"\\n\")", source);
    }

    [Fact]
    public void Normalising_a_crlf_script_leaves_no_carriage_returns()
    {
        // The transformation the class applies, asserted on its own: a CRLF script
        // becomes LF, and a lone CR (an old-Mac checkout, or a hand-edited file)
        // becomes a newline rather than being left to break the next line.
        const string script = "set -eu\r\nexec 3< \"$1\"\rfind .\n";
        var normalised = script.Replace("\r\n", "\n").Replace("\r", "\n");

        Assert.DoesNotContain('\r', normalised);
        Assert.Equal("set -eu\nexec 3< \"$1\"\nfind .\n", normalised);
    }

    [Fact]
    public void In_container_helpers_are_checked_out_with_unix_line_endings()
    {
        // The same bug, one level out: `lunos-atspi` and `lunos-browser` are
        // scripts in the repository that podman runs inside a container. A CRLF
        // checkout makes `#!/usr/bin/env python3\r` an unrunnable interpreter path,
        // and the failure surfaces as "the browser helper exited 126" rather than
        // as anything mentioning line endings.
        //
        // distro/images/profiles and distro/images/desktop/theme each had a
        // .gitattributes; distro/images/desktop itself did not, which is exactly
        // where both helpers live.
        var images = Path.Combine(FindRepositoryRoot(), "distro", "images");
        var offenders = new List<string>();

        foreach (var path in Directory.EnumerateFiles(images, "lunos-*", SearchOption.AllDirectories))
        {
            var content = File.ReadAllText(path);
            if (content.Contains('\r'))
            {
                offenders.Add($"{Path.GetFileName(path)}: carriage returns");
            }
            if (!content.StartsWith("#!", StringComparison.Ordinal))
            {
                offenders.Add($"{Path.GetFileName(path)}: no shebang");
            }
        }

        Assert.True(offenders.Count == 0,
            "In-container helpers must be LF with a clean shebang. Offenders:\n" + string.Join("\n", offenders));
    }

    [Fact]
    public void The_directory_holding_those_helpers_pins_its_line_endings()
    {
        // The lint above only sees THIS checkout. The .gitattributes is what makes
        // it true on someone else's.
        var attributes = Path.Combine(
            FindRepositoryRoot(), "distro", "images", "desktop", ".gitattributes");

        Assert.True(File.Exists(attributes), $"Expected {attributes} to pin eol=lf.");
        Assert.Contains("eol=lf", File.ReadAllText(attributes));
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null &&
               !File.Exists(Path.Combine(directory.FullName, "WorkspaceRuntime.sln")) &&
               !File.Exists(Path.Combine(directory.FullName, "WorkspaceRuntime.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new DirectoryNotFoundException("Could not find repository root.");
    }
}
