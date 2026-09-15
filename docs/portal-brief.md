# Lun.Os End-User Portal

## UI competition brief and implementation plan

### 1. Product boundary

The interface that exists today remains the **Root/Admin Control Plane**. Do not redesign or replace it during this experiment.

Create a separate **End-User Portal** for ordinary Lun.Os users. It should feel like a personal AI workspace, not a server dashboard and not a traditional Linux desktop.

The portal contains only four primary concepts:

1. **Chat** — the main way to ask for work and see results.
2. **Files** — documents, images, spreadsheets, generated artifacts, uploads, and recent files.
3. **Messages** — conversations, notifications, approval requests, and messages from people or agents.
4. **Widgets** — small interactive applications that agents can create or invoke inside the workspace.

Administration, infrastructure, containers, system logs, policies, runtimes, models, secrets, and tenant management remain in the Root/Admin Control Plane.

### 2. Product principles

- **Chat is the center, not the whole product.** Files, messages, and widgets must be first-class and reachable without prompting the assistant.
- **Progressive disclosure.** The default experience is simple. Detail appears only when the user opens it.
- **Safe authority is visible.** When an agent needs permission, the user sees a clear request describing the action, target, and consequence.
- **No technical security language in normal use.** Say “Allow this assistant to send the email?” rather than exposing broker, sandbox, runtime, container, or policy terminology.
- **Every action has a state.** Users can distinguish queued, working, waiting for approval, completed, failed, and cancelled work.
- **Artifacts outlive conversations.** Generated files and useful widget state remain accessible from Files and can be attached to another chat.
- **Responsive by composition.** Mobile is not a scaled-down desktop. Panels become routes, sheets, or tabs.
- **Thin client.** Business logic, agent execution, policy evaluation, approvals, audit, and durable state remain behind Lun.Os APIs.
- **One identity.** Web and installed clients use the same account, sessions, files, conversations, and permissions.

### 3. Shared information architecture

#### Desktop and tablet

- Left navigation rail:
  - Home
  - Chat
  - Files
  - Messages
  - Widgets
- Middle workspace:
  - active chat, file browser, message thread, or expanded widget
- Right context panel, shown only when useful:
  - current task/activity
  - related files
  - active widget
  - approval request
  - details/metadata
- Global command button or command palette for: New chat, upload file, new message, open widget, and search.

#### Mobile

- Bottom navigation:
  - Home
  - Chat
  - Files
  - Messages
- Widgets open from Home, Chat results, or an Apps button.
- Context panels become full-height sheets or dedicated routes.
- Composer remains reachable above the keyboard and supports attachments and voice.

### 4. Required screens and states

Every competitor must implement the same minimum prototype:

1. **Home**
   - greeting and universal prompt
   - continue recent work
   - recent files
   - recent messages
   - pinned widgets
   - active task status

2. **Chat**
   - conversation list/history
   - streaming response state
   - attachments
   - tool/activity status expressed in user language
   - inline file result
   - inline widget result
   - approval request
   - cancel/retry controls

3. **Files**
   - recent, shared, generated, and uploaded files
   - grid/list switch
   - preview/details
   - attach to chat
   - share, rename, move, download, and delete actions
   - destructive actions must ask for confirmation or approval

4. **Messages**
   - people and agent/system conversations
   - unread state
   - notifications and approval inbox
   - link from a message to its related task, file, or widget

5. **Widgets**
   - widget gallery
   - pinned widgets
   - one compact widget on Home
   - one inline widget in Chat
   - one expanded widget view
   - permission request before a widget first accesses a protected capability

6. **Responsive views**
   - desktop at 1440×900
   - mobile at 390×844

### 5. My UI proposal: “Living Workspace”

The portal opens on a calm **Today** screen rather than an empty chatbot. A large prompt field says “What would you like to get done?” Beneath it, a compact activity strip shows work currently running or waiting for the user.

The rest of the page uses modular cards:

- **Continue** — two or three recent conversations with their latest artifact or next action.
- **Files** — visual previews of recent and newly generated documents.
- **Messages** — unread human messages and agent approval requests, clearly separated.
- **Pinned widgets** — live mini apps such as Today’s schedule, task list, expense summary, form progress, or server-free personal utilities.

The central interaction pattern is a **workspace canvas**:

- Chat stays in the primary column.
- A file or widget opens beside it instead of replacing the conversation.
- The user can ask for changes while seeing the artifact.
- Completed results can be pinned to Home or saved to Files.

Visual direction:

- warm dark graphite or soft off-white surfaces rather than “cyberpunk Linux” styling
- one restrained lunar accent color
- generous spacing and readable typography
- rounded but not toy-like cards
- animation only for state transitions, streaming, and moving work between chat and the canvas
- technical metadata hidden behind Details

Permission requests appear as small, concrete action cards:

> **Send “September proposal” to Anna?**  
> This will share `September-Proposal.pdf` outside Lun.Os.  
> **Allow once** · **Always allow for Anna** · **Deny**

Do not show a generic “Agent requests email.send capability” dialog to an ordinary user.

### 6. Widget model for the prototype

A widget is a small, declarative Lun.Os mini app—not arbitrary privileged code.

For the prototype, model a widget with:

- id, name, icon, and version
- compact, inline, and expanded presentation modes
- JSON input/state/output
- declared actions
- declared capabilities
- loading, empty, ready, error, offline, and permission-required states

The host owns navigation, authentication, theme, permissions, persistence, and network mediation. A widget requests narrow actions from the Lun.Os capability layer. Absence of permission means deny.

### 7. Technical recommendation

Use a **responsive React + TypeScript web application/PWA as the canonical portal UI**. It gives Lun.Os a real browser portal and the strongest ecosystem for chat interfaces, file previews, responsive layouts, and composable widgets.

Use **Tauri 2 as an optional native shell**, not as the definition of the UI architecture:

- web: deploy the React/PWA directly
- Windows/macOS/Linux: wrap the same frontend with Tauri when native installation or OS integration is useful
- iOS/Android: evaluate the Tauri shell against required device features; keep the PWA available

Keep all platform-specific features behind typed adapter interfaces so another shell can be introduced without rewriting the portal.

Suggested boundaries:

- `portal-core`: domain types, state, API clients, permissions, and shared behavior
- `portal-ui`: responsive components and widget host
- `platform-web`: browser/PWA adapters
- `platform-tauri`: desktop/mobile Tauri commands and native integrations
- `widget-sdk`: manifest, lifecycle, capability requests, and render modes

Use the existing ASP.NET Core API direction and SignalR for streaming updates. The frontend must not contain agent-runtime logic or bypass the Lun.Os capability broker.

### 8. Why Tauri is not “true multiplatform” by itself

Tauri 2 targets Windows, macOS, Linux, iOS, and Android using a web frontend inside a native shell. It does **not** replace the separately deployed browser version of the portal. Therefore the correct strategy is **web-first UI plus optional Tauri packaging**.

Alternatives to evaluate after the UI experiment:

| Option | Browser portal | Desktop | iOS/Android | Main advantage | Main tradeoff |
| --- | --- | --- | --- | --- | --- |
| React PWA + Tauri 2 | Yes | Yes | Yes | Best web/widget ecosystem and progressive adoption | Native integration needs adapters and Rust/Swift/Kotlin at the edges |
| Flutter | Yes | Yes | Yes | One visual framework across all targets | Dart stack and less natural DOM/web-widget composition |
| Uno Platform | Yes, WebAssembly | Yes | Yes | C#/.NET end-to-end and strong fit with current team skills | Smaller web UI ecosystem than React |

Recommendation for the prototype: **React/PWA first; add Tauri only after the winning UX is selected.** Also build one small Uno spike later if maximizing C# code ownership becomes more important than the web/widget ecosystem.

### 9. Competition rules

Create all branches from the same current commit:

- `experiment/portal-ui-codex`
- `experiment/portal-ui-kimi`
- `experiment/portal-ui-deepseek`

Each agent receives this exact brief and the same seed data. Agents may choose their own visual direction, component library, layout, and interaction details but must not change the four primary product concepts or add admin functionality.

Each entry must deliver:

- runnable interactive prototype
- desktop and mobile layouts
- all required screens and states
- README with run instructions
- short design rationale
- screenshots of Home, Chat, Files, Messages, Widgets, and one permission flow
- no backend dependency required for evaluation; use the shared mock data/API contract
- no changes outside its experiment branch

The winner is selected on experience and architecture, not code quantity.

### 10. Shared demo scenario

Use the same scenario in every prototype:

1. Joche asks: “Prepare the September client proposal from my notes and latest pricing.”
2. Lun.Os shows that it is reading two authorized files.
3. A proposal preview appears beside the chat.
4. A pricing comparison widget appears inline.
5. The user edits a value in the widget; the proposal preview updates.
6. Joche asks Lun.Os to send the result to Anna.
7. Lun.Os presents a concrete approval card because the file will leave the system.
8. After approval, the task completes and the sent document remains in Files.
9. Messages contains a linked confirmation and any reply appears in the same context.

### 11. Evaluation scorecard

Score every entry from 1–10 in each category:

| Category | Weight |
| --- | ---: |
| Clear and pleasant everyday UX | 25% |
| Chat, files, messages, and widgets feel like one system | 20% |
| Responsive desktop/mobile behavior | 15% |
| Permission and approval clarity | 15% |
| Widget model and extensibility | 10% |
| Accessibility and keyboard/touch usability | 10% |
| Implementation quality and maintainability | 5% |

Do not reveal one competitor’s implementation to another until all entries are complete.

### 12. Copy/paste prompt for each coding agent

You are participating in a blind UI competition for Lun.Os. Read `LunOS_End_User_Portal_UI_Competition_Brief.md` completely and implement your own end-user portal proposal.

Start from the current commit and create only your assigned branch. Do not redesign the existing Root/Admin Control Plane. The new portal is for ordinary users and has exactly four first-class concepts: Chat, Files, Messages, and Widgets.

Implement the full shared demo scenario, every required screen and state, and responsive desktop/mobile layouts. Use the shared mock API/data so the prototype runs without a live backend. Keep agent execution and permission decisions behind interfaces; the UI must not bypass the Lun.Os capability layer.

Your visual design and interaction model should be original. Optimize for clarity, calmness, accessibility, and a feeling that chat and artifacts share one continuous workspace. Do not copy generic ChatGPT, Slack, Windows, or macOS layouts wholesale.

Before finishing:

1. Run formatting, linting, type checking, tests, and the production build.
2. Verify the shared scenario at 1440×900 and 390×844.
3. Capture the required screenshots.
4. Add a README with commands, architectural decisions, limitations, and a short design rationale.
5. Commit all work to your assigned branch without merging it.

Assigned branch: `<INSERT ASSIGNED BRANCH>`

When complete, report the commit SHA, run command, verification results, screenshot paths, and any incomplete requirement.

### 13. Recommended execution order

1. Freeze this brief and a small typed mock-data contract.
2. Create the three experiment branches from the same commit.
3. Run Codex, Kimi, and DeepSeek independently.
4. Build and capture every entry using the same viewport sizes and scenario.
5. Evaluate blindly with the scorecard.
6. Select the strongest UX; separately select any architectural ideas worth combining.
7. Create a clean integration branch and implement the selected design against real Lun.Os APIs.
8. Only after UX selection, test packaging the portal with Tauri 2 and run a small Uno Platform technical spike.

