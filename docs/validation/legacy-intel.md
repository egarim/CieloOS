# Legacy Intel validation

From a reviewed checkout on existing Ubuntu, run `bash distro/preflight.sh` as your normal user. No sudo or network is required. This reports inventory only, not installation readiness. Ubuntu 24.04 x64/ARM64 is the initial baseline. An x64 CPU without AVX2 requires a compatible inference build; the report does not prove that build works. Do not run the privileged installer until the inventory and existing service-account/subordinate-ID conflicts have been reviewed.

Run `bash tests/preflight-smoke.sh` on Linux for the inventory smoke test. Physical MacBook and model inference: NOT TESTED.
