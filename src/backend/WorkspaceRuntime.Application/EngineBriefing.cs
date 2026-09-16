namespace WorkspaceRuntime.Application;

// The last paragraph of an agent's goal — the one that tells it how to actually do
// work — differs by engine, and until now it did not.
//
// The native console loop reaches a shell, so "use websearch, python3, the files in
// ~ and ~/shared" is true for it. A hosted foreign engine gets the same sentence and
// none of those things: openclaw.engine.json sets tools.allow to ["cielo__*"], an
// ABSOLUTE allowlist that replaces the profile's defaults, and its own note says so —
// "which is what leaves the agent with no shell and no file write of its own". So the
// engine was told to use four capabilities it does not have, while holding fifteen it
// was never told about.
//
// Worse, eleven of those fifteen take an "id" argument naming the session to act in,
// and nothing ever gave it one. Asked on a live box: browser.navigate, browser.click,
// browser.back, console.type, desktop.click, desktop.double_click, desktop.type,
// desktop.key, recorder.start, recorder.stop and session.destroy all require it. That
// left three tools it could actually call — the spreadsheet ones — which is not a
// model quality problem and explains the benchmark better than anything about the
// model did.
//
// The id was never missing from the host: EngineRun.SessionId has carried it the whole
// time, into a container named after it. It simply never reached the text the engine
// reads.
public static class EngineBriefing
{
    // The native loop's tail. A constant rather than a literal in Program.cs so that
    // Retarget below cannot silently stop matching it — a string-replace against a
    // sentence somebody later reworded would fail by doing nothing, and the symptom
    // would be an engine quietly going back to being told about python3.
    public const string NativeTail =
        "If it needs work on the machine, use your tools (websearch, python3, the files in ~ and " +
        "~/shared), then give the answer. ";

    // What is true for an engine driving the machine through MCP.
    public static string ForeignTail(string sessionId) =>
        "If it needs work on the machine, use the cielo tools you were given over MCP. Those are " +
        "the only tools you have here: no shell, no python, no web search and no way to write a " +
        "file directly. To look something up, drive the browser — browser.navigate, then " +
        "browser.click. " +
        $"You are already inside a session and do not need to create one. Its id is \"{sessionId}\". " +
        "Every tool that asks for an \"id\" wants exactly that value, copied as written. " +
        "Then give the answer. ";

    // Swap the tail for one that is true of this engine.
    //
    // Appends rather than silently doing nothing when the native tail is absent: a
    // goal that never had it (a direct /api/sessions/{id}/agent-run, say) still needs
    // the session id, and an engine with no instructions at all is worse than one
    // with instructions in an odd order.
    public static string Retarget(string goal, string sessionId)
    {
        var foreign = ForeignTail(sessionId);

        // Already done. Without this, a goal that took the append branch and passed
        // through twice would carry two session ids and no way to tell which is
        // meant — the one mistake here that would be worse than the original bug.
        if (goal.Contains(foreign, StringComparison.Ordinal))
        {
            return goal;
        }

        return goal.Contains(NativeTail, StringComparison.Ordinal)
            ? goal.Replace(NativeTail, foreign, StringComparison.Ordinal)
            : goal + "\n\n" + foreign;
    }
}
