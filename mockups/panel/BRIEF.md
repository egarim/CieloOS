# Design brief: the CieloOS admin panel

Produce **one self-contained HTML file** that mocks up the operator's panel. Opened
directly in a browser — no build step, no npm, no server. Static markup with whatever
CSS and inline JS it needs to feel real; no network calls.

## Who this is for — exactly one person

The machine's **root operator**. Nobody else ever opens it. Not a platform team: one
person, possibly the founder, installing CieloOS on a laptop or a VPS for a team of
five. They are technical enough to run an installer over SSH and not much beyond that.

The people who actually *work* in CieloOS use a completely different app,
`portal.html`, which already exists and is not your concern. Projects, chat, files,
desktops — all theirs, none of it here.

## The operator's whole job

In the order they hit it:

1. **Create organizations.** An organization is the isolation boundary. Its slug
   prefixes every person in it (`acme-maria`), which is why it is capped at 12
   characters.
2. **Create people inside one**, and get them able to sign in. Creating a person also
   creates their agent, in the same transaction, with its own separate home. The
   operator never creates an agent.
3. **Configure models** — an AI provider and which model serves each capability.
   Nothing agentic works until this is done.
4. **Know the machine is healthy**, and see what agents have been doing — failures
   first.

## The honest state of the backend

The proposal this mock accompanies is `docs/panel-redesign.md`. Read it first. It is
explicit about what does not exist yet, and the mock must be too:

- **Invitations are designed and NOT built** (`docs/invites.md`). There is no
  endpoint. Today a new person gets a permanent identity token that has to be emailed
  — which is precisely what invitations exist to replace. Show the invitation UI, and
  show it honestly as the thing that is coming.
- **There is no health endpoint.** A card reading "Everything is good" would be a
  claim nothing supports. Say what is actually known and name the rest as unchecked.
- **Audit is ownership-scoped** with no machine-wide owner view yet.

A mock that quietly draws over the gaps is worse than one that shows them, because
somebody will build it and discover them one at a time.

## What the panel must show

Five places: **Overview, Organizations, Models, Activity, System**. Examples and Desks
are gone — Examples was a demo, and per-person work belongs in the portal.

Fill it with plausible content, not lorem ipsum: an organization called `acme` with
five people, one of whom cannot sign in yet; a DeepSeek provider serving chat with no
vision model configured; a couple of failed agent runs; a build version like
`v0.1.9-29-gcdd35ce`.

## The first-run path matters most

A brand-new machine. The operator has just claimed it and has nothing. What do they
see, and what is the shortest honest route to "the team lead and her five people can sign in"?
An empty panel that does not say what to do first is the most common way this fails.

## Constraints

- Light and dark both work, or one that is deliberate and says so.
- No words the operator would have to look up: no "surface", "desk profile",
  "home volume", "principal", "bus".
- Open with an HTML comment stating the central idea in two or three lines, and the
  main trade-off you made. The existing mockups under `mockups/portal/` do this.

## Names

Do not name a real person anywhere in this mock. Use the neutral `acme` organization
and refer to people by role — "the team lead", "a teammate" — or by plainly fictional
first names. Personas we use when talking through scenarios are stand-ins for a real
customer; a mock that says "Create the Yulia organization" reads as product copy, and
gets built that way.
