# CONTINUE.md

> Session handoff. Read this first when picking the project back up.

## Where we are

**Phase 4 ✓ COMPLETE.** All nine chunks plus rollup shipped:
repositories for type + version + junctions + validations +
validation-doc junction; admin list page; detail page with header,
type-level edit, version selector, version-level edits; all four
tabs filled (Workflows placeholder for Phase 5, Required Documents,
Validations, Selection Prompt); state transitions (Create new Draft,
Place in Service with atomic prior-In-Service supersede). Next:
Phase 5 (Workflow Designer). Design conversation first — the chunk
plan in PLAN.md §"Phase 5" is provisional and explicitly expected
to evolve.

Read these to get oriented:
- `docs/PLAN.md` — the phase/chunk roadmap. **Next step is Phase 5
  — Workflow Designer.** Provisional chunk list; expect a design
  conversation before starting Chunk 1.
- `docs/data-model.sql` — the reviewed schema.
- `docs/CONCEPT.md` — design intent. §3.3 covers Settings, User Groups,
  Users, Required Documents, and the Request Type editor in detail;
  §3.1 and §3.2 still scheduled for refresh in Phase 6 / Phase 9.
- `BUILD.md` — how to build/run locally. "What's currently built
  (Phases 1-4)" summarises the shipped surface.
- `LessonsLearned.md` — twelve entries.
- `docs/REMOVE-BEFORE-PROD.md` — debug identity shim cutover checklist.

## Approach rules (locked in during design)

- One commit per chunk. Push directly to `main`, no PRs.
- Chunks are small enough that each leaves the app runnable and adds one
  testable thing.
- No throwaway scaffolding. If a UI surface is needed before the real UI is
  ready, use a temporary test button against the real repository — never a
  mock that gets thrown away.
- Tests live with the chunk that produces the code being tested (where
  there's something meaningful to assert).
- Doc updates at the end of every phase: one commit covering `BUILD.md`,
  `CONTINUE.md`, `CONCEPT.md` (if affected), `LessonsLearned.md`, and
  `PLAN.md`.

## Stack (locked in during design)

- **.NET 10**, Blazor Server, MudBlazor.
- **Dapper** + raw T-SQL. No EF. No migration runner — schema is hand-applied
  from `docs/data-model.sql` against the dev SQL Server.
- **SQL Server** (dev DB, name `VenSure`).
- **Serilog** with file sink, daily rolling, 30-day retention.
- **xUnit** for tests.
- **MailKit/MimeKit** for SMTP (when we get to email).
- **Official Anthropic SDK** for Claude calls.
- **No Docker** for dev. **No CI** — sync and build locally.
- **No `.NET Aspire`.**

## Solution structure (locked in during design)

```
src/
├── VendorSure.Domain/             ← entities, enums, value objects, exceptions
├── VendorSure.Services/           ← orchestration, AI service interface, repos
│                                     defined as interfaces
├── VendorSure.Infrastructure/     ← EF-free data access (Dapper), storage,
│                                     MailKit, Claude client
├── VendorSure.BackgroundWorkers/  ← Windows Service: workflow engine + budget
│                                     polling worker
└── VendorSure.UI/                 ← Blazor Server host, MudBlazor, SignalR
tests/
└── one project per src/ project
```

Dependency direction: `UI` and `BackgroundWorkers` → `Services` →
`Infrastructure` → `Domain`. `Domain` references nothing.

## Sandbox / tooling notes (carried forward from earlier session)

### .NET 10 SDK install (Ubuntu Noble)

The SDK is published in Ubuntu Noble's main archive — no Microsoft repo
needed. Both `archive.ubuntu.com` and `security.ubuntu.com` are in the
egress allowlist.

```
apt-get update && DEBIAN_FRONTEND=noninteractive apt-get install -y dotnet-sdk-10.0
```

`sudo` may or may not be needed depending on whether the session starts as
root. In this sandbox it does not — running as root.

Installs to `/usr/lib/dotnet/sdk` with the CLI symlinked at `/usr/bin/dotnet`.

The first `apt-get install` after a fresh sandbox can fail with 404s on
slightly-stale URLs because of patch bumps between the cached index and the
live mirror — `apt-get update` first, then retry.

### Important limitation: NuGet is blocked in the sandbox

`api.nuget.org` is **not** in the egress allowlist. Consequence: `dotnet
restore` and everything downstream (`build`, `publish`, `test`, `run`) cannot
fetch packages from the sandbox.

The SDK is still useful in-sandbox for:

- Static inspection of source files.
- Template generation (`dotnet new ...`).
- CLI-shape checks (project file structure, etc.).

**Build verification has to happen on the user's local machine**, not in the
sandbox. Workflow: I write and commit code; user pulls, runs `dotnet build`
and `dotnet test`, reports back.

## Suggested next session

**Phase 5 — Workflow Designer.**

The Phase 4 detail page's Workflows tab is the landing surface; what
happens inside it is the Phase 5 work. Per PLAN.md's risk note this
is the biggest phase by far and the only one with unfamiliar UI work
(JS interop for a canvas library). The chunk list in PLAN.md §"Phase 5"
is **provisional and expected to evolve** — start with a design
conversation, not by jumping into Chunk 1.

Key open questions to settle before coding:

- **Canvas library.** PLAN mentions `react-flow`, `jsPlumb` Community,
  or a pure-SVG approach. Blazor Server + JS interop is the harness.
  Worth a spike (Chunk 3 in PLAN) before committing.
- **Block catalog seed.** Phase 5's blocks are IT-authored externally.
  For development, what's the minimum seed set we need to design
  meaningful test workflows? PLAN mentions "a small handful inserted
  manually for testing" but doesn't name them.
- **Coordinates persistence.** The schema stores node coordinates;
  Phase 5 Chunk 8 saves them. Decide early whether layout is
  per-user or shared.
- **Validation posture.** The designer is intentionally a dumb canvas
  (per CONCEPT.md §3.3). What's the smallest set of structural checks
  that *do* belong in the editor, if any (e.g. "every node must have
  an incoming edge except Start")? Or do we genuinely accept that
  broken graphs fail at runtime?

PAT note: each session, user provides a short-lived PAT for the repo.
