using WorkspaceRuntime.Infrastructure;

namespace WorkspaceRuntime.Tests;

// Getting the decision out of whatever the model wrapped it in.
//
// The loop used to require the entire reply to BE one JSON value. A real run died
// on that: two good searches in, the model appended a stray bullet, and the owner
// received
//     model error: '-' is invalid after a single JSON value. Expected end of data.
// as the agent's answer. Nothing was wrong with the work — only with the wrapping.
public class ModelReplyTests
{
    [Fact]
    public void A_bare_object_comes_back_unchanged()
    {
        const string reply = "{\"done\":true,\"note\":\"finished\"}";

        Assert.Equal(reply, ModelReply.FirstJsonObject(reply));
    }

    // The exact failure that shipped.
    [Fact]
    public void Trailing_prose_after_the_object_is_dropped()
    {
        const string reply = "{\"done\":false,\"text\":\"ls\"}\n- and then I will check the folder";

        Assert.Equal("{\"done\":false,\"text\":\"ls\"}", ModelReply.FirstJsonObject(reply));
    }

    [Fact]
    public void A_fenced_block_is_unwrapped()
    {
        const string reply = "```json\n{\"done\":true,\"note\":\"hello\"}\n```";

        Assert.Equal("{\"done\":true,\"note\":\"hello\"}", ModelReply.FirstJsonObject(reply));
    }

    [Fact]
    public void A_preamble_before_the_object_is_dropped()
    {
        const string reply = "Sure, here is the next action:\n{\"done\":true,\"note\":\"ok\"}";

        Assert.Equal("{\"done\":true,\"note\":\"ok\"}", ModelReply.FirstJsonObject(reply));
    }

    // The one that makes naive brace-counting dangerous. Shell commands contain
    // braces constantly — awk programs, ${VAR} expansions, find -exec {} \; — and
    // closing the object at the first } inside a string would truncate the very
    // command the loop is about to type.
    // An UNBALANCED brace inside a string. The first version of this test used
    // `find -exec rm {} \;`, whose braces are balanced — so naive counting reached
    // the same answer and the test passed against the broken implementation too.
    // A lone closing brace is what actually discriminates.
    [Fact]
    public void A_lone_brace_inside_a_command_does_not_end_the_object()
    {
        const string reply = "{\"text\":\"echo '}' >> ~/notes.txt\",\"done\":false,\"submit\":true}";

        var extracted = ModelReply.FirstJsonObject(reply);

        // Counting braces without tracking strings closes at the one inside echo
        // and returns {"text":"echo '}' — which parses as nothing, or worse, as a
        // truncated command the loop then types.
        Assert.Equal(reply, extracted);
        Assert.Contains("\"submit\":true", extracted);
    }

    // Likewise for escapes: the brace has to sit between two ESCAPED quotes, so an
    // implementation that toggles on every quote closes the string early, decides
    // the brace is structural, and truncates.
    [Fact]
    public void An_escaped_quote_does_not_close_the_string_early()
    {
        const string reply = "{\"text\":\"python3 -c \\\"print('}')\\\"\",\"done\":false}";

        var extracted = ModelReply.FirstJsonObject(reply);

        Assert.Equal(reply, extracted);
        Assert.Contains("\"done\":false", extracted);
    }

    [Fact]
    public void Nested_objects_close_at_the_right_brace()
    {
        const string reply = "{\"a\":{\"b\":1},\"done\":true}  then some noise";

        Assert.Equal("{\"a\":{\"b\":1},\"done\":true}", ModelReply.FirstJsonObject(reply));
    }

    // A truncated reply must not yield a fragment. Half an action executed is worse
    // than no action: the loop would type a command the model never finished
    // writing.
    [Theory]
    [InlineData("{\"done\":false,\"text\":\"ls")]
    [InlineData("no json here at all")]
    [InlineData("")]
    [InlineData(null)]
    public void Nothing_usable_returns_null(string? reply)
    {
        Assert.Null(ModelReply.FirstJsonObject(reply));
    }
}
