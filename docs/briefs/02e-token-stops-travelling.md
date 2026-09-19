# Brief 02e — the identity token stops travelling

This is the point of everything in `docs/invites.md`. Everything before it was
scaffolding.

Today, creating a teammate returns their **identity token**, and the owner emails
it. That credential never expires, cannot be revoked, is not single-use, and
authenticates its holder as a full human principal on every route. The safest path
onto the machine has been the least convenient one, so the least safe credential
is the one that travels.

It is safe to change now, and it was not this morning:

- an invitation can be minted, listed and revoked (02b-1)
- it can be redeemed, over a wire proven confidential, spent exactly once (02b-2)
- a compromised account can be closed, with sessions **and** API keys revoked
  (02c)
- there is a screen to redeem it on (02d)

## 1. `POST /api/users` returns an invitation

Instead of `{ slug, token }`, return `{ slug, code, expiresAt }`.

Mint it the same way `POST /api/invites` does — supersede first, 72 hours, audit
it. Consider factoring the shared path rather than writing it twice; two mint
sites that drift is a bug nobody would look for.

**The `0600` token file is still written on the box.** The agent and the CLI need
it. It simply stops travelling: nothing returns it over HTTP any more. That
distinction is the whole change, and the comment should say so.

## 2. `cielo-add-user`

It currently prints whatever `POST /api/users` returned. Update it to print the
code and its expiry, and a sentence saying to send the person a link of the form
`<your panel address>/portal.html#invite=<code>`.

**Do not compose the link for them.** The machine cannot know its own public URL —
a DNAT rewrites the destination before the packet arrives, so even
`SSH_CONNECTION` reports the post-translation address, and `Request.Host` is
attacker-controlled. Print the code and say where it goes. The same reasoning is
already written at `cielo-claim`'s sign-in message; match it.

It lives in an **unquoted** heredoc in `distro/install.sh`, so `$` and `\` are
eaten at install time and must be escaped. Check before you write one — this has
broken twice in this repository.

## 3. The panel must not render a token that no longer exists

`main.tsx` renders `result.token` in two places under "Share this with them". When
the response shape changes those become `undefined`, silently, on the screen an
owner uses to onboard somebody.

Replace both with the code, its expiry, and the same "send them
`…/portal.html#invite=<code>`" sentence. Minimum viable is fine — a full
Invitations card is a separate piece of work.

Also in `ModelsView`, the Security card says *"Setting your first password must be
done on the machine itself"*. That is about to be true only for the owner and
confusing for everyone who just set theirs through a link. Make it *"Your first
password is set on the machine itself, or through an invitation link. Changing it
ends every other session."*

The panel has **no i18n** — strings are inline there, unlike the portal.

## 4. Tests

1. `POST /api/users` returns a code and **no token field at all**. Assert the
   token is absent, not merely unused: a field nobody reads is a field somebody
   will read later.
2. The token file is still written to disk for the new user.
3. The returned code redeems successfully end to end.
4. Creating a user twice supersedes the first invitation.
5. Anything in the suite that asserted the old `{ token }` shape is updated — and
   **say so loudly if you find one**, because a test asserting the old contract is
   how this would silently half-ship.

## 5. What to tell me

`docs/invites.md` §9 lists panel work beyond this (an Invitations card with
resend/revoke/suspend). Out of scope here. List what you did not do.

## Rules

Backend suite is at 579 and must not drop. Frontend: 34 tests, 9 files — your
sandbox blocked esbuild last time, so if it does again, say so and I will run it.

`bash -n distro/install.sh` must be clean.

Do not commit. Do not touch `distro/install-quiet.sh`,
`distro/scripts/cielo-install-tui.sh`, `distro/scripts/cielo-install-frames/`,
`distro/scripts/build-release.sh`, `distro/RELEASE-README.md`, `mockups/portal/`.
