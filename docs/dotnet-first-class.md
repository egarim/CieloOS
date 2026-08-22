# .NET and PowerShell First-Class Support

Lun.Os should treat .NET, C# scripting, and PowerShell as native automation surfaces for office agents.

The goal is not just "install .NET." The goal is:

```text
Agent
  -> structured tool request
  -> policy engine
  -> sandboxed executor
  -> .NET / C# script / PowerShell
  -> audit log
```

## What Ships in the Distro

The .NET profile should include:

- .NET SDK and ASP.NET Core runtime;
- PowerShell 7+;
- .NET 10 file-based C# apps for project-free scripting;
- language server support for C# editing;
- package cache policy for offline or semi-offline use;
- structured agent tool manifests;
- sandboxed execution policies.

Ubuntu 26.04 supplies .NET 10 for ARM64 from its built-in archive. PowerShell uses Microsoft's official ARM64 release archive because the Microsoft package feed does not provide the appropriate Ubuntu ARM64 package. The archive version and SHA-256 digest are pinned in the distro profile, verified by the media builder, and bundled into the runtime setup ISO for installation without GitHub access.

C# scripts use native .NET 10 file-based apps:

```bash
dotnet run --file automation.cs
```

This avoids an additional scripting host and supports package, project, and SDK directives directly in a single `.cs` file.

## First-Class Tools

Agents should not run arbitrary shell strings. They should request structured tools:

```text
dotnet.new
dotnet.build
dotnet.test
dotnet.run
dotnet.script
powershell.run
nuget.restore
```

Each request should carry structured arguments, for example:

```json
{
  "toolName": "dotnet.test",
  "arguments": {
    "project": "workspace/reporting/Reporting.Tests.csproj",
    "configuration": "Release"
  }
}
```

## Security Policy

Default rules:

- `dotnet.build`: allow inside the user workspace.
- `dotnet.test`: allow inside the user workspace.
- `dotnet.run`: require approval unless the project is trusted.
- `dotnet.script`: require approval for network, file writes outside workspace, or package restore.
- `powershell.run`: require approval by default.
- `nuget.restore`: require approval when network access is needed.

Everything should be audited:

```text
agentId
userId
workspaceId
toolName
arguments
policyDecision
sandboxProfile
exitCode
duration
stdout/stderr summary
created files
```

## Sandboxing

Execution should happen in an isolated workspace using a sandbox profile:

```text
workspace mounted read/write
system directories mounted read-only
network disabled by default
CPU and memory limits
execution timeout
clean temporary directory
secret mounts only when explicitly approved
```

The executor can start simple with `podman` or `bubblewrap` and later move to stronger isolation.

## Developer Experience

Lun.Os should feel excellent for both humans and agents:

- terminal profile with `dotnet`, `pwsh`, and common templates ready;
- VS Code or lightweight editor support;
- preinstalled project templates for agent tools and office automations;
- local NuGet cache;
- examples for Excel/report generation, PDF generation, and document workflows.

## Rename Safety

The tooling is branded in the UI as part of Lun.Os, but stable identifiers stay neutral:

```text
tool ids: dotnet.build, powershell.run
service ids: agent-executor.service, workspace-session.service
package profiles: dotnet-automation
```

Do not create durable names like `lun-dotnet` or `LunPowerShell`.
