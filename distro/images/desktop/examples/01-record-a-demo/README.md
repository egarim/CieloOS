# Watch the agent film itself working

The agent starts a screen recording of this session, does a small task on the
desktop, stops the recording, and leaves you an MP4 in `~/recordings`.

## What to watch for

**A red RECORDING marker appears in the corner.** It is there because you might
walk up to a desktop that is already being filmed, and you should not have to be
told. If it is ever missing, the machine says so in the audit line rather than
quietly filming anyway.

**The recording is a file, and only a file.** It stays in your home. Nothing sends
it anywhere, and there is no command on this machine that can — deliberately, since
a recording is thousands of screenshots.

**It refuses a short file.** If the display resizes mid-capture — which happens
when someone connects a viewer — the recorder reports a failure instead of handing
you footage that quietly stopped half way. Underneath, the tool it uses exits
successfully in that case, so the machine checks the encoded length rather than
believing it.

## Afterwards

The MP4 is in `~/recordings`, downloadable from the panel's Files view. Editing it
into a narrated tutorial — chapters, captions, callouts — is the next thing being
built.
