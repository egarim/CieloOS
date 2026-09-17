using System.Net;
using WorkspaceRuntime.Api;

namespace WorkspaceRuntime.Tests;

// Part one of 01b changes no behaviour: the four gates in Program.cs still
// call IsLoopback, and OnThisMachine is not wired in anywhere. This test pins
// IsLoopback's answers for the inputs the gates actually pass it, so that a
// later commit which switches the gates has to change this file on purpose
// rather than by accident.
public class IsLoopbackUnchangedTests
{
    [Theory]
    [InlineData("127.0.0.1", true)]
    [InlineData("::1", true)]
    [InlineData("::ffff:127.0.0.1", true)]
    [InlineData("10.0.0.1", false)]
    [InlineData("0.0.0.0", false)]
    [InlineData("::", false)]
    public void IsLoopback_still_returns_what_it_returned_before(string address, bool expected)
    {
        Assert.Equal(expected, TransportFacts.IsLoopback(IPAddress.Parse(address)));
    }

    [Fact]
    public void IsLoopback_of_null_is_false()
    {
        Assert.False(TransportFacts.IsLoopback(null));
    }
}
