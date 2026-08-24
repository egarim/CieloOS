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
