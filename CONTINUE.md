# CONTINUE.md

> Session handoff. Read this first when picking the project back up.

## Where we are

**Phase 3 in progress.** Chunk 1 done (DocumentType repository with
delete-when-unreferenced rule). Next: Phase 3 / Chunk 2 (Required
Documents Library admin page).

Read these to get oriented:
- `docs/PLAN.md` — the phase/chunk roadmap. **Next step is Phase 3 / Chunk 2.**
- `docs/data-model.sql` — the reviewed schema.
- `docs/CONCEPT.md` — design intent. §3.3 reflects the Settings,
  User Groups, and Users admin pages; §3.1 and §3.2 still scheduled
  for refresh in Phase 6 / Phase 9.
- `BUILD.md` — how to build/run locally. "What's currently built
  (Phases 1-2)" summarises the shipped surface.
- `LessonsLearned.md` — running log of gotchas. Six entries (the
  Phase 2 cross-table-rules entry now also covers Phase 3 Chunk 1's
  delete rule, same pattern).
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

**Phase 3 / Chunk 2 — Required Documents Library admin page.**

Per `docs/PLAN.md` Phase 3 Chunk 2: `/admin/required-documents` route.
MudTable list + New/Edit dialog + Delete button (with confirmation).
Same pattern as the Phase 2 admin pages. Maps Create/Update/Delete
outcomes to distinct snackbar messages — including the
`RejectedReferenced` case for delete attempts on doc types referenced
by Request Type versions.

After this chunk, Phase 3 wraps with a docs rollup, then Phase 4
begins (Request Types, the largest phase so far with 9 chunks).

PAT note: each session, user provides a short-lived PAT for the repo.
