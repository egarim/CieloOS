using WorkspaceRuntime.Application;

namespace WorkspaceRuntime.Tests;

public class HomePathTests
{
    [Theory]
    [InlineData(null, "")]
    [InlineData("", "")]
    [InlineData("/", "")]
    [InlineData("Desktop", "Desktop")]
    [InlineData("/Desktop/notes.txt", "Desktop/notes.txt")]
    [InlineData("Desktop//notes.txt", "Desktop/notes.txt")]
    public void Sanitize_produces_a_safe_relative_path(string? input, string expected)
    {
        Assert.Equal(expected, HomePath.Sanitize(input));
    }

    [Theory]
    [InlineData("../../etc/passwd", "etc/passwd")]
    [InlineData("Desktop/../../../root", "Desktop/root")]
    [InlineData("..", "")]
    [InlineData("./../.", "")]
    public void Sanitize_strips_traversal_components(string input, string expected)
    {
        // '..' and '.' are dropped, never allowed to escape the home root.
        Assert.Equal(expected, HomePath.Sanitize(input));
        Assert.DoesNotContain("..", HomePath.Sanitize(input));
    }
}
