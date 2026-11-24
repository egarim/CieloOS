# Native-first milestone

Coordinates [#53](https://github.com/egarim/CieloOS/issues/53) and [#54](https://github.com/egarim/CieloOS/issues/54). Product direction: [cognitive OS vision](../cognitive-os-vision.md).

## Parallel tracks
A. Audit actual Creatura/Vessel and CieloOS APIs, define authoritative resource ownership, implement smallest native cognitive execution through existing governed command bus. Use deterministic fake model for CI. Do not bypass policy or invent a second runtime.
B. Extend existing inference registry with optional local model, CPU ISA detection, pinned llama.cpp and model integrity checks. Implement no-model mode and explicit local/cloud/hybrid policy. Liquid AI 1.2B quantized candidate; verify model license and runtime compatibility before distribution.
C. Build non-destructive installer/preflight and acceptance harness. Existing Ubuntu host remains intact; no disk formatting, no bootloader changes, no automatic privileged install. Report prerequisites and ask before changes. Headless default, desktops on demand.
D. Specify application lifecycle contract for future private Singular provider. Do not access or distribute private code without authorization. Keep provider-neutral contract and deployment approval boundary.

## First working slice
Clean supported Ubuntu environment -> install CieloOS -> register native Creatura engine -> create agent and workspace -> discover governed file/terminal capability -> execute deterministic task -> independent filesystem oracle -> correlated audit -> management view. Denied action must fail through all exposed routes. Fake model first, local model second. Record exact commit, provider and test outcomes.

## Physical device handoff
Owner will supply MacBook Pro 2012 hardware inventory (16 GB RAM, Ubuntu already installed). Until then use CI and ordinary x64 Linux fixtures; do not claim legacy CPU validation. Read-only commands: cat /etc/os-release; lscpu; free -h; lsblk; df -h /. Check AVX/AVX2/FMA, free disk and RAM before runtime build. Do not assume GPU acceleration. Benchmark local model separately before running full stack. Capture model hash, runtime flags, TTFT, tokens/s, RSS, available RAM, swap, thermal data if available, task outcomes. Initial target 5 decode tokens/s and no sustained swap, not a certification.

## Definition of done
Versioned reproducible installation instructions, pinned dependencies, deterministic CI, actual native execution evidence, deny-by-default test, offline mode with no silent cloud fallback, cancellation and cleanup, uninstall/rollback instructions, explicit PASS/FAIL/BLOCKED/NOT TESTED report. Keep unfinished ecosystem services explicitly unavailable rather than simulate production success. No automatic merge.

## Deferred
ISO, Incus migration, full desktop, multi-agent scheduling, private Singular deployment, graph experiments and fleet management follow the first vertical slice. Existing providers can be migration baselines, not alternative product architecture.
