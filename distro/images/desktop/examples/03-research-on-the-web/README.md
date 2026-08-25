# A real browser, and a real decision

The agent opens a page in a genuine Chromium on this desktop — you can watch it —
reads the page's own structure rather than pixels, and follows a link.

## What to watch for

**It will stop and ask you.** Opening an address is the moment "fetch a page" can
become "send data somewhere", and the destination may have been chosen by text the
agent read on a previous page. So a human approves it. This is the example that
shows you the gate; the others mostly avoid it.

**A click cannot walk around that gate.** Clicking a link that leaves the site is
refused and reported, rather than quietly becoming a navigation nobody approved.
You will see that happen in this example: the agent tries, is refused, and asks
properly.

**The browser is not yours.** The agent works in its own profile, so a hostile page
cannot reach the sites you are signed in to. That separation is also the only thing
that works — a modern Chromium refuses to be automated against your real profile,
which is the same conclusion for the same reason.

## What this does not do

Once a page is open it can talk to its own servers as any web page can. The
approval covers where the agent GOES, not everything a page does after it gets
there. Enforcement that holds continuously is being built.
