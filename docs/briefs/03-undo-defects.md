# Brief 03 — two verified defects in the undo machinery

Both were found in an audit and both are still open. They are small, they are
security, and one of them has been quoted as the cautionary example in four
commit messages today while remaining unfixed.

## 1. `/api/version/{id}/restore` is agent-reachable by omission

`Program.cs:1493` maps `POST /api/version/{id:guid}/restore`. `Security.cs` has
no rule matching it, and `AccessPolicy.Required` falls through to
`AnyPrincipal`.

So **an agent can restore a home volume** — which, combined with defect 2, means
an agent can restore its owner's home. `Ownership.CanAccessHome` is the isolation
boundary of this product: a human reaches its own and its owned agents' homes, an
agent reaches only its own. A restore that bypasses it is that boundary failing.

Decide the right level and defend it in a comment. `HumanOnly` is the obvious
answer — undo is a person's judgement about their own work, not an action an
agent should take on anyone's behalf. If you conclude otherwise, say why.

Check `GET /api/version/history` in the same breath: it is listed nowhere either,
and "which versions exist of whose home" is not obviously public.

**Table both in `AccessPolicyTests`.** The lesson this defect teaches is that an
unlisted route fails silently, so the fix is not complete until a test would fail
without it.

## 2. The snapshot takes the wrong home

`RuntimeServices.cs`, around line 327:

```csharp
await versionStore.RecordBeforeAsync(
    store.GetUser(request.UserId).Slug, request.Id,
    $"{request.ToolName}.{request.Operation}", cancellationToken);
```

The comment above it says "Snapshot the **owner's** home". Establish what
`request.UserId` actually is when an **agent** issues the command — the agent's
identity, or the human that owns it — and what `RecordBeforeAsync` does with the
slug it is given.

The defect as recorded: when an agent acts, this snapshots the wrong volume. An
agent works in its own home; a checkpoint of somebody else's home before an
agent's destructive action captures data that is not about to change and leaves
the data that IS about to change uncaptured. Restoring it then writes over a home
nobody touched.

**Verify this before changing it.** Trace the call path and say in your summary
what `request.UserId` holds in both cases — human-initiated and agent-initiated.
If the current code is correct and the audit was wrong, say that instead and
change nothing but the comment, which is at best ambiguous.

If it is wrong, fix it and write a test that fails without the fix: an agent
performing a snapshot-worthy action must checkpoint the agent's home.

## Rules

Build and run the suite until green:
`dotnet test tests/backend/WorkspaceRuntime.Tests/WorkspaceRuntime.Tests.csproj`.
It is at 569 and must not drop.

Do not commit. Do not touch `distro/install-quiet.sh`,
`distro/scripts/cielo-install-tui.sh`, `distro/scripts/cielo-install-frames/`,
`distro/scripts/build-release.sh`, `distro/RELEASE-README.md` or
`mockups/portal/` — somebody else's uncommitted work is in this tree.

In your final message: the new test count, what `request.UserId` turned out to
hold, and whether defect 2 was real.
