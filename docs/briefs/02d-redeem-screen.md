# Brief 02d — the screen an invitee actually lands on

The backend is done: `POST /api/invites/preview` and `POST /api/invites/redeem`
are Public, redeem refuses a non-confidential transport, spends atomically and
returns a session cookie identical to a password login's. Nothing in the UI calls
either.

`docs/invites.md` §9 specifies this. Read it.

## 1. It goes in the portal, not the panel

`vite.config.ts` builds two entries: `index.html` → `main.tsx` (the operator
panel, plain CSS, no i18n) and `portal.html` → `src/portal/` (the four-slot nav a
teammate was actually hired to use, with `src/frontend/src/i18n/{en,es,ru}.json`
behind it).

Putting redemption in the panel would drop a new teammate into "Manage this
machine" — the wrong home, and an untranslated one.

The link is `…/portal.html#invite=<code>`. A **fragment**, not a path: there is no
`MapFallback` in `src/` — the panel is `UseDefaultFiles` + `UseStaticFiles` only —
so `/invite` would 404. A fragment also never reaches the server, which is the
right place for a credential to live in a URL.

## 2. The hash is read above every other gate

This is the part that is easy to get subtly wrong.

Read `#invite=` **before** the stored-token check and **before**
`/api/setup/status`. A browser holding a stale `runtime.token` in `localStorage` —
a reused kiosk, a shared laptop, the owner clicking the link to see what it looks
like — must still reach the invitation screen rather than being silently signed in
as somebody else.

And `history.replaceState` must not have destroyed the fragment before it is read.
Check the existing entry code for one.

**Redemption clears any stored identity token**, so a shared machine does not
leave the next person holding the last person's permanent credential.

## 3. The screen

**Preview first.** Call `/api/invites/preview` and show who they are about to
become — display name, organization — before asking for anything. Nobody should
choose a password for an account they cannot identify.

**Then the password**, twice, minimum ten characters. Match the server's rule, and
say the rule before they get it wrong rather than after.

**Dead links get their state**, in the server's vocabulary: `used`, `revoked`,
`superseded`, `expired`. Each needs a sentence that says what to do next — "ask
for a new link" — not just a status word. An unknown code and an expired one look
identical by design; do not invent a distinction the server deliberately refuses
to make.

**On success** the cookie is already set, so go where a signed-in teammate goes.
Do not ask them to sign in again with the password they just chose.

## 4. `SignIn.tsx`

`src/portal/SignIn.tsx` still offers "sign in with an identity token" and calls
`writeToken()`. **It stays** — an install upgraded from before passwords has
nothing else. But its copy stops telling people to find a token file and starts
telling them to ask the owner for an invitation link.

## 5. i18n

Every new string goes in **all three** of `en.json`, `es.json`, `ru.json`.
`shared/i18n-parity.test.ts` fails on a key added to one and not the others, so
this is not optional and you will find out immediately.

Russian matters here specifically: this is the flow a teammate whose name cannot
even be slugified goes through, which is why `--user` exists.

## 6. Tests

Match whatever the portal already does — there are `.test.tsx` files beside the
components.

1. `#invite=` is read before the stored-token path, with a stale token present.
2. A dead invitation shows its state and a next step.
3. An unknown code and an expired one render the same thing.
4. A short password is refused client-side without calling redeem.
5. Success clears any stored identity token.
6. i18n parity holds.

## Rules

Your sandbox could not start the frontend runner last time — esbuild was denied
while loading `vite.config.ts`. If that happens again, say so plainly and do not
claim the tests pass. `npm run build` and the frontend tests will be run here.

The backend suite is at 579 and must not drop.

Do not commit. Do not touch `distro/install-quiet.sh`,
`distro/scripts/cielo-install-tui.sh`, `distro/scripts/cielo-install-frames/`,
`distro/scripts/build-release.sh`, `distro/RELEASE-README.md`, `mockups/portal/`.

Final message: files changed, whether the frontend build/tests ran or were
blocked, and any place §9 and the current code disagree.
