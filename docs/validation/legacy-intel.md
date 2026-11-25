# Legacy Intel validation

From a reviewed checkout on existing Ubuntu, run `bash distro/preflight.sh` as your normal user. No sudo or network is required. This reports inventory only, not installation readiness. Ubuntu 24.04 x64/ARM64 is the initial baseline. An x64 CPU without AVX2 requires a compatible inference build; the report does not prove that build works. Do not run the privileged installer until the inventory and existing service-account/subordinate-ID conflicts have been reviewed.

Run `bash tests/preflight-smoke.sh` on Linux for the inventory smoke test. Physical MacBook and model inference: NOT TESTED.

The inventory reports `service_account`, `subuid` and `subgid`. Existing accounts or mappings require manual review; `conflict` means the installer's proposed fixed range overlaps another allocation; `unknown` means the file could not be read or parsed. `clear` is only a local-file check, not authorization to install. NSS-backed allocations and existing mappings require separate review. No installer changes are made.
