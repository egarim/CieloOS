# Agent-Guided Installation

The installation agent is a guide and planner. It does not receive a shell and it cannot write a partition table directly.

## Control Path

```text
User conversation
  -> installation agent
  -> workspace-installer CLI
  -> immutable installation plan
  -> deterministic validation
  -> human approval bound to the plan hash
  -> trusted installer worker
  -> Ubuntu Subiquity/curtin
  -> audit record
```

The core boundary is the deterministic CLI, not a particular agent protocol. A future MCP adapter may expose the same commands, but it must not add broader capabilities.

## CLI Commands

The CLI writes JSON to standard output and errors to standard error. The agent principal may invoke the first four read-only commands:

```bash
workspace-installer defaults
workspace-installer probe
workspace-installer plan --request request.json --probe probe.json
workspace-installer validate --plan plan.json --probe probe.json
```

The trusted onboarding UI or an authenticated human console session invokes the approval command:

```bash
workspace-installer approve --plan plan.json --probe probe.json \
  --token PLAN_ID --approved-by USER
```

`--request -` and other file arguments accept `-` for standard input. Tests may supply a saved probe so plans are reproducible without access to real disks.

## Defaults

- Use the only eligible internal disk; require an explicit choice if more than one exists.
- Use the entire selected disk with encryption enabled.
- Keep SSH disabled.
- Install .NET and PowerShell.
- Select the local Prism Bonsai profile and its weights.
- Disable cloud inference fallback.
- Require a human to approve the exact SHA-256 plan identifier.

The plan identifier binds approval to the exact plan content; it is not itself an authorization secret. Process permissions and the policy layer must deny the `approve` command to the agent principal. Passwords, recovery keys, and provider credentials do not enter the agent conversation or installation plan. A trusted UI collects secrets separately.

## Execution Boundary

The current V0.1 CLI implements probing, default selection, planning, validation, and approval artifacts. Its tool contract marks approval as unavailable to agents. The next installer worker will translate only a validated and approved plan into Subiquity configuration. It will expose fixed operations rather than arbitrary command strings.

## Try Mode

For local VM evaluation, `./distro/scripts/vm-run.sh --try` boots the prepared reference system with a temporary QEMU snapshot. Changes made during that session are discarded when the VM shuts down.

The distributable milestone will publish a reference VM image beside the installer ISO. This provides a complete pre-install trial while the ISO remains the path for installation onto physical or virtual disks.
