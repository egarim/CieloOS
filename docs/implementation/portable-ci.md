# Portable CI
The repository owns test logic; CI providers provision tools and invoke it.
From any directory, invoke the repository's scripts/ci/test.sh with Bash.
On Ubuntu with .NET SDK 10, Node 22, npm and standard shell tools:
```sh
bash scripts/ci/test.sh backend
bash scripts/ci/test.sh frontend
bash scripts/ci/test.sh preflight
bash scripts/ci/test.sh all
```
Nonzero exit means failure; unknown suite exits 2. No privileged installation is performed.
Frontend restores dependencies and writes build output; use a disposable checkout for untrusted code.
GitHub Actions and Woodpecker invoke the same backend/frontend logic. The existing preflight workflow is retained pending coordination with PR #56.
Woodpecker requires a separately configured server and isolated Docker workers; do not enable privileged execution or expose host credentials to PR jobs. Images use major-version tags initially; pin digests before production rollout.
Release packaging and full OS acceptance remain separate follow-up work. No physical hardware or Woodpecker deployment is certified by this change.
