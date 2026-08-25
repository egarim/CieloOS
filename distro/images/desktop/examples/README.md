# Examples

Four things this machine can do, that you can run rather than read about.

Each folder holds a short README and an `example.json`. The JSON is the example —
it names the surfaces it uses and the goal it pursues, and the agent runs it
through the same policy-checked bus as everything else. Nothing here is a special
demo path: if an example works, the machine works.

| | what it shows |
|---|---|
| `01-record-a-demo` | The agent does a task **and films itself doing it**. You get an MP4. |
| `02-drive-the-desktop` | Pointer and keyboard on a real XFCE desktop, grounded on the accessibility tree rather than guessed pixels. |
| `03-research-on-the-web` | A real browser, driven by the page's own structure — and **an approval prompt**, because leaving a site is a decision. |
| `04-build-a-spreadsheet` | A document produced by an agent, opened in ONLYOFFICE, in a format Excel reads. |

## Running one

Open the panel, go to **Examples**, and press Run. The agent works in *this*
session, so you can watch it happen on the desktop you are looking at, and take the
mouse back at any time.

## Expect to be asked

Example 03 stops and asks your permission partway through. That is not a rough
edge — it is the point. Some actions on this machine are decisions, and the agent
does not make them alone. Watching one happen tells you more about how the machine
works than this paragraph does.

## If an example fails

Say so, and keep the output. These examples are also how a fresh installation
proves it works on your hardware: a failure here is a real bug on this machine, not
a demo that needs coaxing.
