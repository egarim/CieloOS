# CieloOS admin panel: simplified information architecture

## Decision

Treat `main.tsx` as a control plane for one person: the machine's root operator. Remove the consumer and demo surfaces. The panel should answer five questions in order:

1. What needs my attention?
2. Which organizations exist, and who belongs to each one?
3. Can those people sign in?
4. Which model serves each capability?
5. Is the machine healthy, and what have its agents done?

"Team" is not used as a product noun in this proposal. An **organization** is the isolation boundary recorded in a user's `OrgSlug`. A **project** is a collaboration record with members and tasks. Projects remain in `portal.html`; they do not belong in this operator panel.

## Navigation

1. **Overview** — the operator's queue: Organizations, People awaiting invitation, Model status, Machine health, and recent agent failures.
2. **Organizations** — create organizations and manage the people and invitations inside each one; putting these together prevents creation flows from drifting apart.
3. **Models** — configure providers and choose the default provider/model for each capability.
4. **Activity** — inspect agent and operator activity across the machine, with filters and failure-first defaults.
5. **System** — inspect machine health, desk-image readiness, engine confinement, version, sign-in security, and integration keys.

Removed navigation and where its function goes:

- **Examples** is deleted, including its API polling and approval UI. It is a scripted demo, not an operator task; no function moves.
- **Desks** is deleted from the admin panel. Files, chat, interactive desktops, agent task entry, and per-person work belong to `portal.html` or purpose-built operational tooling. Pending approvals move to Overview and Activity because they genuinely require operator attention.
- **People** is folded into Organizations. A person cannot be created without an organization, so a machine-wide independent creation form encourages the wrong mental model.
- **Machine** becomes System. Its current aspirational cards are replaced by observed state and explicit unsupported states.
- **Home** becomes Overview. The consumer greeting, “Ask your assistant,” and inspiration cards are removed.
- The **“Manage this machine”** nav group is removed. The only panel user is always the owner, so every item is already machine management.

## Screens

### Overview

What the operator sees:

- **Organizations**: count, total people, and a `Create organization` action.
- **Onboarding**: people whose invitation is live, expired, used, or missing; the primary warning is “N people cannot sign in yet,” not a count of permanent tokens.
- **Models**: one row per capability (`chat`, `vision`, and `embedding` when configured), showing the selected provider and model; missing defaults are warnings.
- **Machine health**: a compact healthy/degraded/unavailable summary, build version, and desk images still building or missing.
- **Needs attention**: pending approvals, recent blocked/failed audit events, invitation problems, and unavailable health checks. Successful activity is secondary.

Actions are deliberately shallow: create an organization, continue onboarding, configure a missing model, approve/reject a pending action, or open the relevant detail screen.

Backing calls:

- `GET /api/organizations` for organization and people counts.
- `GET /api/users` for people. The owner receives all organizations; the response already includes `orgSlug`, `deskProfile`, language, and owner status.
- `GET /api/models` for providers and current `chat`/`vision` defaults. The route does not currently expose an embedding default, so the UI must not claim one exists.
- `GET /api/approvals` and `POST /api/approvals/{id}/approve|reject` for the operator queue.
- `GET /api/audit-events?since=...` for recent events. The current route is ownership-scoped and does **not** provide a machine-wide owner view; an owner-capable machine-wide activity route or an owner-specific expansion of this route is required.
- `GET /api/branding` for `buildVersion` and product identity.
- `GET /api/desk-profiles` for image readiness/build state.
- `GET /api/invites` for onboarding status is **designed in `docs/invites.md` but does not exist**.
- `GET /api/health` for a trustworthy aggregate is **new and does not exist**. Until it exists, the card must say “Limited checks” and report only the facts above; “Everything is good” is not supportable.

### Organizations

The default view is an organization list. Each row shows display name, permanent slug, people count, and onboarding counts. The creation form previews the server-derived slug and explains before submission that it is capped at 12 characters, prefixes every subsequently created person's slug, and cannot be renamed.

Selecting an organization opens its detail in the same screen:

- people grouped only under that organization;
- each person's display name, immutable slug, desk profile, and sign-in/onboarding state;
- `Add people` as a repeatable name + desk-profile form;
- newly created invitation links accumulated in a session-local list with `Copy link` per person and `Copy all` for a six-person onboarding run;
- invitation history and actions: send a new link, revoke a live link, and—once implemented—suspend/unsuspend a person;
- an explicit note that creating a person also creates their agent in the same transaction, with a separate persistent home volume. There is no `Create agent` action.

Backing calls:

- `GET /api/organizations` and `POST /api/organizations`.
- `GET /api/users`.
- `POST /api/users` with `{ name, deskProfile, orgSlug }`. Today this returns `{ slug, token }`; the panel should label that behavior as legacy onboarding and should not disguise the permanent identity token as an invitation.
- `GET /api/desk-profiles` and, where an image is missing, `POST /api/desk-profiles/{id}/build`.
- `POST /api/users/{slug}/organization` exists, but moving a person is not a primary action. `OrgSlug`, not the person's slug prefix, is authoritative after a move. Put the action behind a confirmation explaining that the slug and home names do not change. The projects design later says “no moving”; that conflicts with the currently implemented route, so the product decision should be settled before polishing this control.
- `GET /api/invites`, `POST /api/invites`, and `POST /api/invites/{id}/revoke` are **designed but not built**. `POST /api/users` is also designed to return a one-time invitation instead of a token, but does not yet do so.
- `POST /api/users/{slug}/suspend|unsuspend` is **designed but not built**.

There is no organization delete, slug rename, project editor, or agent creator. Projects and their members remain in `portal.html`.

### Models

What the operator sees:

- a capability-first summary: for each supported capability, the default provider, concrete model name, locality, and whether a required key is present;
- a provider list with base URL, kind, locality, capabilities, key-present flag, and built-in/runtime-added status;
- one add-provider form with presets, followed by explicit default selection;
- warnings before removing a provider that currently serves a default capability.

Actions and routes:

- list with `GET /api/models`;
- add with `POST /api/models`;
- remove runtime-added providers with `DELETE /api/models/{id}`;
- choose a default with `POST /api/models/defaults`.

Provider credentials are write-only; the API exposes only `hasKey`, which the UI should preserve. The current API supports provider capabilities `chat`, `vision`, and `embedding`, but OS defaults returned and set by the present UI cover only `chat` and `vision`. Either add embedding to the default contract or describe it as provider capability only.

Model usage belongs here only if it is made operator-wide. `GET /api/usage` currently reports the caller's desk and machine totals, while `POST /api/usage/limits` permits the caller's own user/agents and a loopback-only OS ceiling. It cannot back per-organization or per-person administration. Keep a small machine total if useful; do not present “this desk” as machine administration.

### Activity

What the operator sees:

- newest-first events with time, action, outcome, principal, on-behalf-of identity, and detail;
- default filters of `Blocked`, failures, and pending approvals, with `All activity` one click away;
- filters for organization, person, agent, action, outcome, and time range;
- pending approvals inline with their preview and exact approve/reject action;
- a clear distinction between runtime-audited activity and actions an engine performed inside its own container, which `/api/engines` explicitly says are not visible to the policy bus.

Backing calls:

- `GET /api/audit-events?since=...&until=...&action=...` and `GET /api/approvals`.
- `POST /api/approvals/{id}/approve|reject`.
- `GET /api/users` and `GET /api/organizations` for human-readable filters.

The existing audit route only returns events whose homes pass `Ownership.CanAccessHome` for the caller. The machine owner is not automatically allowed to read every person's or agent's home, so this cannot currently satisfy “what agents on the machine have been doing.” A new owner-only projection such as `GET /api/admin/activity` is required, or `/api/audit-events` needs an explicitly owner-only machine scope. It should return audit records, not grant home/file/session access.

### System

What the operator sees:

- observed runtime health, build version, database/store reachability, session backend availability, disk pressure, and last successful check;
- desk profiles and image build state, with a build action for missing images;
- available agent engines, the tools exposed to them, declared confinement, install blockers, and the API's honest oversight statement;
- operator password/session controls and named integration API keys;
- update, backup, and safe-mode rows only when a real backend reports their state and supplies an action.

Backing calls:

- `GET /api/branding` for the installed build version.
- `GET /api/desk-profiles` and `POST /api/desk-profiles/{id}/build`.
- `GET /api/engines` for engine manifests, exposed tools, confinement, blockers, and installability. It is currently read-only; there is no install route, so this is inspection, not an installation wizard.
- `GET /api/keys`, `POST /api/keys`, and `DELETE /api/keys/{id}` for the signed-in operator's integration keys. The `GET` response also contains sessions, although `main.tsx` currently discards them.
- `POST /api/auth/password`, `POST /api/auth/logout`, and `POST /api/auth/logout-all` for operator security.
- `GET /api/health` is **new and required** for the aggregate health claims above. No current route reports database, disk, backup, update, or safe-mode state.

Owner actions must retain the real signed-in session requirement from `AccessPolicy.OwnerOnly`. An identity token or API key is insufficient, even when it resolves to the owner.

## First run

### What a newly claimed machine shows

After `POST /api/setup/claim`, land on a setup version of Overview rather than the ordinary dashboard. The claim currently returns an identity token, not a password session, so the first checklist item is mandatory:

1. **Set the operator password.** On the local machine or SSH tunnel, call `POST /api/auth/password`, then sign in through `POST /api/auth/login`. This is required before any `OwnerOnly` organization or user action will work.
2. **Add a model** if the organization needs agents immediately. This is useful but not a prerequisite for account creation or sign-in.
3. **Create the organization.** Enter its display name, review the <=12-character permanent slug preview, and call `POST /api/organizations`.
4. **Add Yulia and five people.** Stay on the new organization's detail and submit six `POST /api/users` calls with the organization slug and desk profile. Each call creates the person and their agent together; no agent step appears.
5. **Copy six invitation links** and send them out of band. Each recipient redeems in `portal.html`, sets a first password, and receives a normal session.

That is the shortest intended route to “Yulia and her five people can sign in”: claim -> set owner password/sign in -> create one organization -> create six people -> copy six links. Model setup can happen before or after onboarding.

It is **not possible today**. Step 5 waits on the invitation backend described in `docs/invites.md`: no `/api/invites` endpoint exists, and `POST /api/users` returns a permanent identity token. Until invitations ship, the setup checklist must say “Remote sign-in invitations are not available on this build” and, if legacy onboarding remains exposed, call the returned value a permanent identity token with an explicit warning. It must not render an enabled `Invite` button.

## What to delete from `main.tsx`

These are feature estimates, not a line-by-line patch plan. Shared auth/fetch helpers and reusable approval/audit rendering are excluded where they survive.

| Feature deleted | Approximate lines | Notes |
|---|---:|---|
| Examples types, polling, runner, approval controls, nav/wiring, and Home links | 175 | The `ExamplesView` block alone is about 158 lines. |
| Desks workspace: desk rail, file browser, shared files, session lifecycle, desktop inhabiting, chat launcher, console task runner, and per-desk activity | 650–750 | Most of lines 760–923 and 1071–1624 plus associated state/types/effects. |
| Duplicate “Add teammate” flow inside Desks | 90–120 | Already overlaps the Desks estimate; remove rather than merge. It also omits an organization and falls back to the founding organization. |
| Consumer Home greeting, assistant CTA, activity teaser, inspiration cards, and Examples callbacks | 110–130 | Replace with the operator Overview rather than editing its copy. |
| Current Machine placeholder cards | 30–35 | Replace with observed System state. |
| Personal “this desk” usage/budget UI inside Models | 45–60 | Reintroduce only machine/operator-appropriate usage under Models or System. |
| Dead types/state/imports left by those removals | 40–70 | Includes unused legacy types and feature-only state. |

Because some categories overlap, the expected unique deletion is roughly **950–1,100 of 2,537 lines** before adding the new Overview, organization detail/invitations, Activity filters, and System health views. The target is not a particular final line count; it is removing whole jobs that belong to the portal or a demo.

## Considered and rejected

### Keep the current seven tabs and only rename them

Rejected because the problem is scope, not labels. Desks and Examples invite the operator to behave like an end user and demo runner. Machine contains promises without state. Keeping them preserves the same cognitive load and duplicate creation paths.

### Separate Organizations and People as top-level screens

Rejected because every person must have an `OrgSlug`, and the organization prefix affects the permanent person slug. A separate global add-person form hides the most consequential choice. The machine-wide people search still exists inside Organizations, but creation begins from an organization.

### Call organizations “teams”

Rejected because it would collide with two real concepts. Organizations are isolation boundaries; projects have members and are where collaboration happens. A friendly alias would eventually make “move team” ambiguous and could encourage treating project membership as home access. The UI says Organization.

### Put Projects in the admin panel

Rejected because projects are managed by leads and members in `portal.html`. The root operator's stated job is provisioning organizations and people, not managing their work. Duplicating project membership rules in `main.tsx` would also create a second authorization-sensitive UI.

### Keep Desks as an observability screen

Rejected in its current form. Desks combines file access, session creation/destruction, impersonation (`Shadow`/`Become`), chat, and task execution. That is operational intervention, not a concise answer to “what have agents done.” Activity should expose audit records; a future incident-response console can be designed separately with explicit authority and purpose.

### Use the current owner token throughout first run

Rejected because `OwnerOnly` now deliberately requires a password-backed session. Letting the claim token create organizations or people would undo that security boundary. The setup flow must set the operator's password and establish a session before provisioning.

### Label the current `{ slug, token }` result as an invite

Rejected because the token is permanent, non-revocable, and not single-use. The UI must not borrow the safety expectations of the designed 72-hour, one-time invitation. Invitations get a first-class surface now, but it stays visibly unavailable until its routes exist.

### Send invitation email from CieloOS

Rejected consistently with `docs/invites.md`: there is no mailer, verified email, bounce handling, or appropriate egress path. The operator copies links and sends them out of band. The panel can offer `Copy all`; it should not imply delivery.

### Add bulk CSV import for six people

Rejected for the first version. It introduces validation, partial-success, and secret-delivery problems for a small operator workflow. A repeatable form with accumulated results and `Copy all` removes most of the friction while preserving one audited `POST /api/users` transaction per person.

### Derive organization membership from the slug prefix

Rejected because the prefix is only a minting rule. `UserRow.OrgSlug` is authoritative, especially after the existing move route is used. No screen should parse a slug to decide tenancy.

### Show a green “healthy” state from successful page loads

Rejected because a working HTTP request says nothing about disk pressure, store health, session backend, backups, or updates. Overview may report limited component checks now; a real health claim waits on a real route.

### Keep credentials and usage inside Models

Rejected because password/session/API-key management is system security, not model configuration, and “this desk” usage describes the signed-in identity rather than the machine. Moving security to System makes Models capability-focused. Usage stays only where its scope can be named honestly.

### Build an engine installation wizard from `GET /api/engines`

Rejected because the route is read-only. It reports whether manifests meet confinement requirements but exposes no install operation. System should show this evidence and its blockers, not a button that cannot be backed or an installation that would appear governed without being so.

## Broken, misleading, or dead in the current panel

- **Examples is explicitly demo code** and remains wired into navigation, Home inspiration cards, `/api/examples`, polling, and approval handling despite having no operator role.
- **The Desks “Add teammate” path is a second, drifting user-creation UI.** It posts no `orgSlug`, so the backend defaults the person into the founding organization. `PeopleView` does require an organization. Two buttons that say nearly the same thing can create materially different identities.
- **The claimed first-run transition cannot perform owner work immediately.** `POST /api/setup/claim` stores an identity token and enters the app, while `OwnerOnly` now requires a real signed-in session. Organization and user creation will return 403 until the operator sets a password locally and logs in; the UI does not guide this sequence.
- **Both current person-creation results instruct the operator to share a permanent identity token.** This is the unsafe onboarding path `docs/invites.md` is designed to replace.
- **Activity is not machine-wide.** `ActivityView` filters to `whoami.homes`, and the server's audit route is also ownership-scoped. The owner therefore cannot see the activity of every person's agent, despite the screen saying “Everything your spaces and assistants have done.”
- **Home claims “everything's good” without a health check.** A configured model is treated as readiness; there is no database, session-backend, disk, update, or backup signal.
- **Machine reports unsupported assertions.** “You're up to date” is unconditional, while Check is disabled; Backup and Safe mode are disabled placeholders. No backing routes exist.
- **Models is also the security and personal-budget screen.** This is information-architecture drift, and the password help still says the first password must be set on the machine even though the designed invitation flow will allow an invitee to set it remotely.
- **`GET /api/keys` returns sessions, but the frontend type and state keep only `keys`.** Session inventory is fetched and discarded, even though sign-out controls sit beside it.
- **Embedding is selectable as a provider capability, but the UI's default model and API response shape expose only chat and vision defaults.** The screen can create a capability it cannot make the OS default through its current controls.
- **`GET /api/engines` exists and was written for an admin area, but `main.tsx` never calls it.** Engine confinement and oversight—the most operator-relevant engine facts—are absent.
- **Several declarations are dead or misleading:** `PlatformUser`, `Workspace`, and `InferenceStatus` are declared but unused; `HomeView` accepts `ownHomes` and never reads it.
- **The production desktop viewing path is knowingly absent.** `watchDesktop` opens the raw viewport port and its comment says a runtime-proxied authenticated route is future work. That is another reason not to preserve Desks as an admin promise.
- **Chat does not follow the selected agent desk when more than one agent exists.** The panel warns that `/v1/agent` resolves the first agent; the selection and action therefore disagree.
- **The Activity screen truncates silently to 12 events.** The API supports time/action queries, but there is no pagination, range control, or indication that older records were omitted.

## Boundary with `portal.html`

This proposal does not modify or redesign `portal.html`. It only makes the boundary explicit: people redeem designed invitation links and do their daily work there; organizations, identities, models, machine health, and machine audit live here. Projects remain portal work, not admin work.
