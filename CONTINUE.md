# CONTINUE.md

> Session handoff. Read this first when picking the project back up.

## Where we are

**Phase 1 complete.** All 7 chunks done (solution scaffold + MudBlazor,
Serilog file logging, DB connection factory, debug identity shim, Settings
repository with tests, Settings list page, Settings edit dialog), plus
the post-Chunk-7 render-mode-cascade fix. Next: Phase 2 / Chunk 1
(UserGroup repository).

Read these to get oriented:
- `docs/PLAN.md` — the phase/chunk roadmap. **Next step is Phase 2 / Chunk 1.**
- `docs/data-model.sql` — the reviewed schema.
- `docs/CONCEPT.md` — design intent. §3.3 updated to reflect the Settings
  admin pattern; §3.1 and §3.2 are still stale for the original reasons
  (refreshed in Phase 6 / Phase 9).
- `BUILD.md` — how to build/run locally. Includes a "What's currently
  built (Phase 1)" summary.
- `LessonsLearned.md` — running log of gotchas. Five entries so far.
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

**Phase 2 / Chunk 1 — UserGroup repository.**

Per `docs/PLAN.md` Phase 2 Chunk 1: Dapper-based repository for the
`user_groups` table. Domain entity in `VendorSure.Domain.Identity` (sits
next to `User.cs`), interface in `VendorSure.Services.Identity`, impl
in `VendorSure.Infrastructure.Identity`. CRUD: list, get by id, create,
update, deactivate. Tests in `VendorSure.Infrastructure.Tests`
following the `SettingsRepositoryTests` pattern (probe an existing
seeded row, restore in `finally`).

The User repository was partially built in Phase 1 / Chunk 4
(`GetByEntraidAsync` for the debug shim). Phase 2 / Chunk 2 grows it
with the rest of the CRUD surface.

PAT note: each session, user provides a short-lived PAT for the repo.
