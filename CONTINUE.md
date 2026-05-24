# CONTINUE.md

> Session handoff. Read this first when picking the project back up.

## Where we are

**Phase 5 in progress.** Chunks 1-4 done (`WorkflowDefinition`
repository + Workflows tab + `WorkflowNode` repository + designer
page shell). The designer route the Workflows tab navigates to
(`/admin/request-types/{typeId}/workflows/{workflowId}/designer`)
now resolves: loads workflow + version + type + nodes via the
repos, renders a breadcrumb + state-aware header + an empty
`<div id="workflow-canvas">` placeholder + a temporary
node-list readout table for development feedback. Next: Chunk 5
(D3 interop spike — first JS interop in the codebase).

Phase 5 design settled before code:
  - **D3.js** for the SVG canvas. No React, no build pipeline,
    one CDN/npm dep on a stable library.
  - **Fixed layout** computed from `execution_level` (vertical row)
    + parent-driven horizontal slots (path1 = left, path2 = right
    consistently). No `x`/`y` columns in the schema.
  - **No designer-side validation.** Schema CHECKs only.
  - **`execution_level`** = topological depth. Designer renumbers
    downstream nodes on insert/delete; engine walks levels in Phase 6+.
  - **Branch merging deferred** — schema permits, editor refuses.
  - **Workflows tab → list page** on the Request Type detail page.
  - **Designer opens on a separate route**
    `/admin/request-types/{typeId}/workflows/{workflowId}/designer`.
  - **Auto-save per atomic edit.** Each insert/drag/delete commits
    its own transaction.
  - **Block + artifact catalog seeded manually on dev DB** by the
    user. Phase 5 code reads them as-is.

Read these to get oriented:
- `docs/PLAN.md` — the phase/chunk roadmap. **Next step is Phase 5
  / Chunk 5 — D3 interop spike.** PLAN's provisional Phase 5
  chunk list was superseded by the design conversation (see Where
  We Are above); the locked-in plan lives in the previous chat
  transcript and on the commit log.
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

**Phase 5 / Chunk 5 — D3 interop spike.**

The riskiest chunk in Phase 5. First JS interop in the codebase
and first non-trivial JS file. Goal: D3 renders a graph inside the
`<div id="workflow-canvas">` placeholder Chunk 4 already provides.

Scope:
  - One `workflow-designer.js` file under
    `src/VendorSure.UI/wwwroot/js/`. References D3 from a CDN
    (probably `https://cdn.jsdelivr.net/npm/d3@7/dist/d3.min.js`,
    pinned version).
  - A small `IJSObjectReference`-based module that the designer page
    instantiates on first render. The module owns the canvas div
    completely (Blazor never re-renders inside it — see
    LessonsLearned about Blazor + D3).
  - Blazor pushes the graph in via a JS function call:
    `module.invokeMethodAsync('mount', { nodes, edges })`.
    Eventually JS will call back into Blazor; not in this chunk.
  - Render: each node as a shape per type (oval / rectangle /
    diamond / terminal-oval) with the seeded color; edges drawn
    as curves via `d3.linkVertical()` from path1/path2 FKs.
  - Layout: fixed, computed from `execution_level` (vertical row)
    + parent-driven horizontal slot (path1 = left, path2 = right
    for Decisions; insertion order otherwise). The layout function
    lives in JS and is dumb — no force simulation, no animation.
  - Read-only in this chunk. No drag, no zoom, no click handlers.
    Just rendering.

Likely catches:
  - Blazor Server's render-diff fighting with D3's DOM mutations.
    Fix: the canvas div has `@key` or a static element ref and
    Blazor never re-renders inside it after the initial mount.
  - First-render timing — Blazor's `OnAfterRenderAsync(firstRender)`
    is the right hook to mount D3 from.
  - D3 v7 vs v6 API differences (selection.enter() / data() pattern).

Wires to Chunk 4's loaded `_nodes` collection. No new repo work.

PAT note: each session, user provides a short-lived PAT for the repo.
