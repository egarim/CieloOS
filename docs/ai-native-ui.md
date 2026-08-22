# AI-Native UI Direction

Decided 2026-08-18 after a six-lens design review (agent-computer-interface prior art, shell architecture, shared-control UX, surface-contract design, small-model constraints, and a devil's-advocate case against a bespoke shell). **Revised 2026-08-22** after a second six-lens research review, when the owner set two new requirements: the OS serves multiple isolated remote desktops for humans and agents alike, and desktop automation — the agent seeing the screen and driving mouse and keyboard — is the main goal. The revision promotes D2's former "legacy tier" to a first-class session tier; the sections below reflect the revised direction and mark what changed.

## Thesis

Typed where possible, pixels where necessary, policy everywhere.

Against first-party surfaces, agents never operate the UI: humans and agents emit the same typed commands onto one policy-checked bus, and the visible UI is a projection of that contract. Against everything else — the world's existing software, running inside isolated desktop sessions — agents drive pixels, but only through a session gateway the runtime owns, under explicit, revocable, recorded input grants.

The reasoning is structural, not aesthetic. Per-click policy over pixels is impossible: a click on "Send" and a click on "Cancel" are the same input event, so no policy engine can mediate them semantically. That argument stands — which is why pixel policy moves one level up, to where decisions are still typed and hashable: who may create a session, who may attach to it, whether input is granted at all, what the session may reach on the network, and when a human takes over. Policy over typed commands remains trivial, which is why the pipeline (`ToolRequest -> PolicyEngine -> SandboxedToolExecutor -> AuditEvent`) is still the whole product: sessions, grants, and takeovers are commands on that same bus.

The frontend already contains the seed of this design: the panel's spreadsheet buttons submit the exact `/api/tool-requests` JSON an agent would emit — though no agent emits it yet (the CLI is chat-only), and the panel's approve/reject and chat controls still bypass the bus. The direction below makes the bus the law rather than a coincidence.

## Design laws

1. **One command bus.** Every mutation — human click or agent token — is a `ToolRequest` through policy, executor, and audit. No UI-only mutation paths. Enforced by a conformance test in the spirit of the rename-safety tests.
2. **Observation is a contract.** Every surface publishes a revisioned, schema-declared state document with change events (JSON Patch). Reads pass policy the same way writes do; observation of another principal's data is itself a privileged operation.
3. **One manifest, many projections.** A single versioned contract schema (unifying `distro/tools/*.json` and `distro/installer/tool-contract.json`) generates: the UI affordances, the policy registration, the JSON Schema that constrains local-model decoding, and the MCP adapter. Manifest/implementation drift fails startup or CI — a hand-maintained parallel contract is the failure mode this project exists to avoid.
4. **Agents never inject input against first-party surfaces** *(revised 2026-08-22)*. No synthetic clicks, keys, or pointer events against the runtime's own surfaces, under any provider or privilege level. Into desktop sessions, synthetic input flows only as typed computer-use commands through the session gateway, under an active input grant — never via in-session automation tools (`xdotool` called by agent code), never via kernel-level injection (`ydotool`/uinput are banned from the image), never by handing an agent the raw display socket. Sessions contain zero agent code.
5. **Consent binds to content.** Approvals carry the exact typed request, a dry-run effect preview, and a content hash; approving binds to that hash, so any mutation of the pending request invalidates consent. The approve verb is structurally denied to agent principals (`requiresHuman`), generalizing the installer's `exact-plan-id` contract to every tool. At the pixel tier, where clicks admit no dry-run, consent binds to the **session spec and grant scope** (image, mounts, egress, duration — all stable and hashable), and mandatory recording plus the input ledger are the compensating controls. Pixel input is audited as batched input frames rather than one event per mouse move — the one documented exception to law 1's granularity, not to its principle.
6. **Small-model discipline.** The default brain is a ~1 GB 4B model. Per turn: at most 8 currently-valid commands, a bounded state document, and grammar-constrained decoding so invalid output is structurally impossible. Reliability is an architecture property, not a model property. The local model plans and drives typed surfaces; it is `vision: false` and must never be the verification channel for a pixel action.
7. **Agent sessions are strangers to human sessions** *(added 2026-08-22)*. An agent principal never obtains input access to a human's session without a hash-bound, time-scoped, revocable grant approved by that human; human takeover atomically suspends the agent's grant before the human's first input lands. Agent sessions are credential-free by default — no logged-in browsers, no saved passwords — because on-screen content is a prompt-injection vector; collaboration happens through shared volumes, artifacts, and the approval flow, not shared cookies.

## Decisions

### D1 — Identity: OS presentation, appliance core

Lun.Os is an operating system. Its architecture is a headless agent appliance — the runtime, policy engine, and surfaces are the product — and its only near-term *host* shell is a kiosk session (`workspace-session.service`, a name already listed among the reserved neutral service ids in `docs/dotnet-first-class.md`) that boots into the web console rendered by the runtime itself. The scope of "no desktop" is the **host**: no first-party compositor, window manager, or GNOME-plus-accessibility bridge runs on the host, because that would create an automation channel below the policy engine. This is not in tension with D3 — *session* desktops (XFCE/Xvnc inside per-principal containers) absolutely exist and are the whole point of the pivot; they live inside the isolation boundary, reachable only through the runtime, never on the host beside the control plane.

The full native-OS ambition (an own shell, potentially an own compositor that enforces the surface contract at the display layer) stays on the long-horizon roadmap. It is gated on two things: the surface contract proving itself across multiple surfaces, and the project outgrowing a single maintainer. The contract is the invariant that keeps this door open — a native shell would be another renderer of the same surfaces, replacing the kiosk without touching the runtime.

### D2 — Scope: surfaces fast, sessions universal *(revised 2026-08-22)*

The 2026-08-18 form of this decision ("surfaces, not apps — no windowed pixel apps, no browser in the guest") is superseded. The owner's requirements — multiple isolated desktops, agents doing real desktop automation — make the former "legacy tier" the funded main track.

The revised scope is two tiers under one policy engine:

- **Surfaces (fast path).** First-party and contract-speaking software exposes typed commands exactly as before: per-command policy, dry-run previews, local-model-drivable, cheap and reliable. Nothing from V0.3 is retired; every future first-party feature is still built contract-first, and inside sessions the agent must always prefer a surface over pixels when one exists.
- **Sessions (universal fallback).** Arbitrary software runs inside isolated desktop sessions. The agent's eyes are policied screenshot reads; its hands are typed computer-use commands translated to input by the runtime's session gateway. Sessions are the container boundary that makes "technically incapable of targeting first-party surfaces" a property rather than a promise: the runtime, its panel, and its API live outside every session's namespace and network.

### D3 — Users and sessions *(added 2026-08-22)*

Humans and agents are both users. Each gets its own isolated desktop session(s); they never share a session by default and collaborate through shared volumes, the approval flow, and invitation — a human may watch an agent's session live, and entering a human's session is always grant-gated (design law 7). The runtime is the session broker: `session.create`, `session.attach`, `session.grant-input`, `session.input`, `session.take-over`, `session.destroy` are schema-2 manifest commands on the existing bus, the `spreadsheet.surface.json` pattern applied to desktops. The React panel evolves into a session dashboard: session cards with thumbnail polling (a policied screenshot read), watch, take-over, and the existing approval feed.

Isolation is decided by hardware, not preference. Nested virtualization is impossible on the M2 dev Mac (Apple gates EL2 to M3+; the QEMU/HVF guest has no KVM), so VM-per-session is unavailable in dev and **Incus system containers** are the session primitive: unprivileged, idmapped, per-NIC default-deny egress for agent profiles, ephemeral roots for agents and persistent home volumes for humans. The same manifest and codepath launch a KVM VM (`--vm`) on future bare metal — but the two are not the same security product: a shared-kernel container and a hardware-isolated VM differ materially in blast radius, and the doc must not let "just a policy field" paper over that. Containers are the accepted dev-and-single-tenant posture; genuine multi-tenant isolation of mutually distrusting principals is a bare-metal-VM guarantee, tested on real hardware before it is claimed. Do **not** build streaming: adopt commodity aarch64 open source (TigerVNC Xvnc as the substrate, KasmVNC/Selkies for delivery) — the runtime owns lifecycle and policy, never codecs. Agents work headless (screenshot in, typed input out); video attaches only when a human opens the viewport, which is what keeps CPU-only encoding viable. Dev-VM capacity is ~2–4 concurrent sessions — a dev-scale limit, not an architecture property; density is a bare-metal milestone.

The session gateway is the single door: a runtime-owned RFB proxy between every session's VNC server and everything else. Agent actions are typed `computer` commands (`screenshot`, `click`, `type`, `key`, `scroll`, …) on the bus, translated to RFB input by the gateway — so attach grants, human-priority takeover (human input flips control-owner for a cooldown; agent input in that window is dropped and audited), input logging, and framebuffer recording are all proxy-level policy, not per-tool hacks. Sessions contain zero agent code; `xdotool`-by-agent and `ydotool`/uinput are banned from the image.

### D4 — Vision models: connect anything, ship something *(added 2026-08-22)*

The OS is model-agnostic; the agent connects to whatever provider is configured, which the existing provider registry already supports. Cloud is the blessed path for pixels — a **hosted computer-use provider**, policy-gated through the approval flow. Exact model and tool-version names (which frontier model, which tool schema) live in provider config and a dated research note, never hardcoded into this direction doc, because they age in months; the architecture commits only to "a hosted computer-use provider behind the registry," not to a vendor. A small local model still ships in the image so the OS is never brainless offline: Bonsai-4B (`vision: false`) runs agent-guided setup, plans, and drives typed surfaces, delegating see-and-click to the vision provider — it must never verify a pixel action. "Screenshot leaves the machine" is its own permission class, separate from "use cloud tokens." One local-VLM experiment precedes any deeper bet: serve MAI-UI-2B (Apache-2.0) host-side as a grounder for Bonsai-planned subgoals, judged against a self-built ~20-task desktop eval.

### D5 — MCP: after manifest unification, generated, capability-neutral

An MCP adapter (exposing tools and surfaces to clients such as Claude Code) ships after the unified contract schema lands, generated from that schema rather than hand-written against today's manifests — the adapter should be written once, not twice. The rule from agent-guided installation applies unchanged: an adapter may re-expose commands, but must not add capabilities, and approval verbs remain human-only regardless of transport.

## Gaps this direction must close

Found during the design review, in priority order. The V0.3 slice closed these for the surface bus (see `docs/local-dev.md` for the mechanics); the `distro/tools/*.json` policy declarations remain unenforced until those tools gain a real executor. The list is kept as the record of what the review found:

- `POST /api/approvals/{id}/approve` is unauthenticated: any local process, including the agent, can self-approve. The human/agent principal split is on the critical path for everything above.
- The observation channel is untyped and unpoliced: the frontend re-fetches unversioned REST snapshots; no revisions, no diffs, no change events, no policy on reads.
- Two policy grammars exist, neither generated from the other: `DefaultPolicyEngine` hardcodes spreadsheet policy in C#, while `distro/tools/*.json` declares tool policy that nothing enforces.
- `ApprovalRecord` identifies its request only by id; the pending `ToolRequest`'s content is stored but never surfaced, so humans consent to a sentence rather than a change.
- `LocalInferenceRouter` forwards only temperature and max tokens; the default provider's llama.cpp `json_schema` constrained decoding is unused.
- The panel has no stable automation identifiers; controls are findable only by visible text, some of which is branding-derived and changes on rebrand.

## V0.3 — the first slice

One surface, both principals, policy-intercepted, diff-observable:

1. **Principal split.** Human session principal required for approvals; agent service principal for tool requests; API bound to localhost with an authenticated path for remote/VM-forwarded use.
2. **Unified contract schema.** Merge the two manifest grammars into one versioned schema; promote the spreadsheet's implicit operations into `surfaces/spreadsheet.surface.json` as the template.
3. **Spreadsheet as the first complete surface.** Revisioned state with ETag, change events, commands with idempotency keys, expected-revision preconditions, and dry-run; the panel becomes a `useSurface()` renderer; `workspace-agent` gains `observe` and `do` verbs driving the identical bus.
4. **Hash-bound approval cards.** Dry-run effect previews rendered from `PolicyEvaluation.Evidence`; approval binds to the request hash; audit events gain correlation ids so the log reads as a narrative.
5. **Constrained decoding.** A response-format field through the inference router to llama.cpp; an available-commands endpoint returning the currently-valid command set that both the agent loop and the panel's buttons render from.
6. **Conformance tests.** CI fails if any frontend mutation bypasses the command bus, or if a manifest and its executor disagree.

V0.4 follows with the generated MCP adapter and the kiosk image (`workspace-session.service`, frontend served by the runtime, virtio-gpu in the VM scripts) — pending a rendering spike for a WPE/Chromium kiosk browser on ARM64.

## What Lun.Os will NOT build *(added 2026-08-22)*

A solo project survives the desktop pivot only by adopting or skipping everything that isn't the differentiator. The differentiator is the control plane: policy-gated sessions, hash-bound grants, human takeover, and one attributed, replayable audit trail. Everything below is explicitly out of scope — used off the shelf or not at all:

- **No streaming/VNC/RDP/WebRTC stack of our own.** Adopt TigerVNC / KasmVNC / Selkies / Guacamole. We own the gateway that proxies them, not the codecs.
- **No session/desktop platform of our own.** Do not deploy or fork Kasm Workspaces, E2B, or Scrapybara as the control plane — their admin planes duplicate the runtime. Mine their components; reject their platforms.
- **No GPU/render farm, no fighting software rendering.** llvmpipe is fine for form-and-button UIs; media-heavy apps and high session density wait for bare metal.
- **No local vision model as the default actor.** Vision is cloud-first; the local VLM is a grounder experiment, not a dependency. We do not train models.
- **No Wayland synthetic-input battle.** X11 (Xvnc) is the substrate; GNOME/KDE portal+libei consent flows are the wrong shape for agent fleets. wlroots+wayvnc is a track-B swap behind the same gateway, not a research project.
- **No nested-VM isolation in dev.** Impossible on the M2; Incus containers now, `--vm` profile on bare metal later. No Kata/Firecracker integration until there is real KVM.
- **No CRIU live-suspend of GUI sessions** (arm64+GUI fragility): "suspend" means freeze-short-term or snapshot+recreate, stated honestly in the contract.
- **No per-mouse-move policy evaluation.** Policy at grant granularity, audit at input-batch granularity — or SQLite drowns.
- **No agent input into human sessions without a grant, ever** (design law 7). No credentials/logged-in browsers in agent sessions.

## Red-team caveats to keep visible *(added 2026-08-22)*

The research review's adversarial lens raised four things that are true and must not be buried:

1. **Pixel consent is weaker than typed consent.** Per-click policy is impossible and per-session grants risk becoming rubber stamps; recording + input ledger + egress limits are the honest compensating controls and must ship *together* with grants, not later.
2. **The headline feature is cloud-dependent and metered.** Long-horizon desktop tasks cost real money per hour and today's success rates are bimodal — near-solved for short tasks, ~20% full-success for long ones, leaving half-completed GUI state with no undo. The surface tier's transactional guarantees are the contrast that justifies the split; do not let pixel unreliability contaminate trust in surfaces.
3. **Visual prompt injection is unsolved.** On-screen content instructing the agent succeeds at alarming rates; credential-free agent sessions and per-session egress limits bound the blast radius, and the design-law-7 rule against agents in credentialed human sessions is the primary defense.
4. **Positioning risk.** If the README leads with computer-use, Lun.Os is benchmarked against Anthropic/OpenAI/Kasm/E2B on their turf. It leads with the control plane — the policy-native agent OS — and computer-use is a capability underneath it.
5. **The session gateway is the real infrastructure** *(second-opinion review, 2026-08-22)*. "We don't build streaming" is true, but the runtime-owned RFB proxy — correct input arbitration, grant enforcement, human-priority takeover ordering, recording tee, failure handling, and the security boundary that keeps agents off the host — is the hardest single piece of engineering in the whole pivot, and the one thing that cannot be adopted off the shelf. It is the core engineering risk, not a proxy detail; budget for it accordingly. (Codex's phrase: the gateway "becomes the product.")

## Revised roadmap *(2026-08-22)*

Requirement 1 (multiple isolated desktops) and requirement 2 (agent desktop automation) are one architecture, sequenced strictly so each step is usable alone:

- **V0.4 — Sessions exist.** `surfaces/session.surface.json` (create/attach/destroy) + a `SessionOrchestrator` executor over the Incus REST API; per-session Incus container with TigerVNC Xvnc + XFCE; the panel becomes a session dashboard; humans get desktops they reach through the runtime. Distro autoinstall gains Incus, a ZFS pool, and a prebuilt session base image.
- **V0.5 — Grants and hands, safely, as one indivisible milestone** *(order corrected 2026-08-22 per second-opinion review)*. Pixel consent is weak, so the compensating controls ship *with* the grant, not after it. This milestone is atomic: `session.grant-input` (hash-bound RequireApproval lease) + `session.input` (the typed `computer` command set) + the session gateway (C# RFB proxy, human-priority takeover) + a minimal input ledger (every injected action logged with principal, grant id, and a screenshot-before hash) + **deny-by-default per-session egress** (nftables) — all landing together, plus the cloud vision provider behind the "screenshot leaves the machine" gate. Two agent sessions proven unable to reach each other or the runtime except via the bus (a conformance test, rename-safety style). The gateway is the single hardest piece of engineering in the pivot (a correct RFB proxy with input arbitration, grant enforcement, failure handling, and security boundaries) and is treated as the core risk of this milestone, not a proxy detail.
- **V0.6 — Full-fidelity recording.** Framebuffer *video* recording (the heavier artifact) correlated into the audit log by session and grant id, and approval cards that embed the screenshot region and intended action. The lightweight input ledger and screenshot-hash audit already shipped in V0.5; this milestone adds replayable video on top.
- **V0.7 — Two-tier resolution.** In-session surface bridge so contract-speaking apps inside a session still get the fast path; the local grounder-VLM experiment and its desktop eval.

Bare metal (real KVM, `--vm` isolation, higher density) is the milestone that turns the dev-scale demo into the multi-tenant claim — not something the M2 dev VM is asked to prove.
