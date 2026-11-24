# CieloOS: the native operating system for artificial cognition

Status: product vision and proposed architecture, not a claim of implemented functionality. Native integration mandate: [#53](https://github.com/egarim/CieloOS/issues/53).

## One infrastructure
CieloOS exists for Creatura. Separate repositories are packaging and development boundaries, not separate products at runtime. Creatura is the default cognitive engine; Vessel owns embodiment and cognitive lifecycle; CieloOS owns governed execution, isolation and resource enforcement. Cognitive graph provides scoped persistent knowledge; Telekinesis and Medium provide perception and semantic accessibility; Sentiora Terminal and Browser provide specialized interaction; Custodia protects credentials; Vivarium provides local and fleet management over one authority; Unisono provides capability interoperability and distribution. Preserve the existing typed command bus, policy and audit spine. Existing agent loops are migration and experimental baselines.

## Native OS primitives
Agents, cognitive runs, workspaces, capabilities, observations, verified outcomes, knowledge, approvals, credentials, budgets and applications are first-class OS resources. Use stable identities, versioned contracts, authenticated caller context, deny-by-default authorization, scoped grants, revocation, correlation IDs and explicit unknown outcomes. Native capability discovery is automatic; MCP is a governed projection, not a second authority. No manual wiring of individual services. One release compatibility manifest, health checks, migration, rollback and end-to-end conformance tests. See #53 for implementation sequence and E01–E16 acceptance tests.

## Application development as a native capability
Proposal: provide a private Singular instance as the preferred application development environment in an appropriately licensed CieloOS edition, potentially with Cosmos Builder. Keep the native development contract provider-neutral; do not require private code for public builds. Evaluate actual Singular interfaces and license before implementation. AGPL obligations require legal review; service separation alone does not guarantee compatibility. Community edition may use standard tools; developer and enterprise editions may provision licensed Singular. Editions and pricing are not decided.

Native application lifecycle: discover need, collect requirements, design data model and capability contract, scaffold, implement, build in isolated workspace, test with independent oracles and simulated users, review security and permissions, request deployment approval, deploy versioned artifact, register capabilities, observe outcomes, propose upgrades, roll back. Never let generated code grant itself permissions or modify the trusted control plane. Separate development and production credentials. Application manifests should describe ID/version, interfaces, capability schemas, permissions, dependencies, accessibility, provenance, audit and rollback. Medium and Unisono interoperability should be evaluated, not assumed.

## Use cases
1. **Custom ERP:** ingest authorized inventory spreadsheets, interview staff, build supplier/purchase/stock workflows, verify inventory arithmetic and role permissions, deploy after approval; another agent queries typed stock capabilities.
2. **Adaptive office:** observe an opt-in recurring competitor research workflow, propose a dashboard and scheduled reporting application; compare manual and automated results before approval.
3. **Software company:** analyst, architect, developer, QA, security, DevOps and support agents work in separate scoped workspaces; Singular builds apps, Vivarium supervises, graph retains verified engineering lessons without leaking customer data.
4. **Medical administration:** scheduling, intake, supply inventory and shift reports; optional voice transcription; professional oversight, consent, privacy and clinical validation required. No autonomous clinical decisions.
5. **Continuous improvement:** observe authorized workflow friction, create isolated candidate version, compare clicks/time/errors with controlled simulated users, deploy only after approval. Measurements are hypotheses, not existing results.
6. **Personal OS:** build travel, receipts, expenses, language-learning or project applications tailored to an individual; sensitive data stays scoped and user-controlled.
7. **Marketplace:** versioned applications, agent profiles, workflows, capabilities, knowledge packs, workspaces and test suites; Unisono and potentially Prometheus; signing, review, licensing and explicit grants.
8. **Agents build tools for agents:** identify missing invoice-processing capability, build/test on synthetic invoices, approve deployment and register reusable typed interface; next agent discovers it without regenerating code.
9. **Cognitive research lab:** matched environments compare legacy agent, Creatura no persistent memory, vector, graph, hybrid and approved capability creation. Independent outcomes, matched budgets, held-out tasks, paired seeds, cost/latency/tokens/security and negative results. No claims of learning superiority before evidence.
10. **Organization-wide OS:** department-scoped workspaces, applications, agents, knowledge and policies; Vivarium federated management; explicit cross-department collaboration. Active Directory analogy is a vision, not existing federation.

## Flagship demonstration
Clean install; create native Creatura agent; user requests inventory app; Singular (if licensed and available) scaffolds and builds; independent tests verify stock arithmetic, access and persistence; human approves deployment; CieloOS registers capability; second agent using a different model discovers and operates app without implementation knowledge. Verify resulting inventory state independently, correlate audit events, test denied access and rollback, retain authorized experience. Record reproducible demo and limitations. First deliver #53 vertical slice before full application demo.

## Linux foundation decision proposal
Keep Ubuntu 24.04 LTS as current baseline, evaluate 26.04 LTS only after compatibility tests. Use minimal server host, not a full desktop on host; desktops and developer environments run in isolated sessions. Keep host OS, cognitive control plane, workload images and user data separate. Pin images/dependencies, use signed release manifests, stage upgrades and rollback, test recovery. Rootless Podman is current baseline; evaluate Incus for system-container desktops and KVM microVMs/VMs for stronger isolation of untrusted workloads; neither is automatically a security guarantee. Avoid a custom kernel or distro fork until evidence requires it. Compare Debian Stable for minimal host, Fedora CoreOS/openSUSE MicroOS for transactional appliance host, NixOS for reproducibility; Alpine musl and Yocto/Buildroot maintenance are poor initial fits. Evaluate native .NET, Node/node-pty, Chromium, AT-SPI, GPU drivers, local inference, networking, container isolation, x64/ARM64 and WSL2 separately. Benchmark idle RAM, boot/readiness, workload latency, image size, patch/rollback and operational effort. No distro migration approved yet.

## Roadmap
1. Native Creatura/Vessel vertical slice and shared contracts (#53).
2. Native perception, terminal, browser, credentials and graph.
3. Application lifecycle contract and private Singular feasibility spike.
4. Flagship app demo and adversarial acceptance tests.
5. Unified release, fleet management and longitudinal experiments.
6. Marketplace and appliance hardening only after evidence.

## Design laws
Typed where possible, pixels where necessary, policy everywhere. One authority per resource. Native by default, modular by implementation. Verified outcomes before durable knowledge. Human governance over deployment and privileges. Independent components, unified distribution. Do not confuse proposals with shipped features.
