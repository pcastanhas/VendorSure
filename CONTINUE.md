# CONTINUE.md

> Session handoff. Read this first when picking the project back up.

## Where we are

**Phase 5 in progress.** Chunks 1-6 done. The designer route
(`/admin/request-types/{typeId}/workflows/{workflowId}/designer`)
now has a left-rail palette: Start + three terminals as fixed
entries, one entry per active `block_catalog` row beneath. Drag
a palette item onto the canvas → the JS module fires a
`DotNetObjectReference` callback → `OnPaletteDropAsync` runs
`WorkflowNodeRepository.CreateAsync` with an orphan-node seed
(level=0, no path FKs) → re-mount with the new graph. First
end-to-end write path from the designer. Palette is hidden when
the version is read-only. Next: Chunk 7 (edge drawing — wire
path1/path2 via UI).

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
  / Chunk 7 — edge drawing (wire path1/path2 via UI).** PLAN's
  provisional Phase 5 chunk list was superseded by the design
  conversation; the locked-in plan lives in the previous chat
  transcript and on the commit log.
- `docs/data-model.sql` — the reviewed schema.
- `docs/CONCEPT.md` — design intent. §3.3 covers Settings, User Groups,
  Users, Required Documents, and the Request Type editor in detail;
  §3.1 and §3.2 still scheduled for refresh in Phase 6 / Phase 9.
- `BUILD.md` — how to build/run locally. "What's currently built
  (Phases 1-4)" summarises the shipped surface.
- `LessonsLearned.md` — fourteen entries.
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

**Phase 5 / Chunk 7 — Edge drawing (wire path1/path2 via UI).**

Second write path from the designer. Goal: drag from one node's
output handle to another node's input handle to set `path1_node_id`
(or `path2_node_id` for Decisions).

Scope:
  - JS module: add small output handles to each non-terminal node
    (one for Start/Process, two for Decision — visibly distinct so
    the user knows which is path1 vs path2). Terminal nodes get no
    output handles (they have no out-edges).
  - HTML5 drag from a handle to a node target. The dataTransfer
    payload carries `{sourceNodeId, slot}` where slot ∈
    {"path1","path2"}. On drop on a node body, the JS module calls
    a new `[JSInvokable] OnEdgeDropAsync(sourceId, targetId, slot)`.
  - Blazor: route to `IWorkflowNodeRepository.SetPath1Async` or
    `SetPath2Async` from Chunk 3 — which already handles renumbering
    via the recursive CTE. On success, reload + re-mount.
  - Result-enum handling: most cases are silent success, but the
    no-merging rule (target already has an incoming edge from
    another parent) needs a clear Snackbar message; same for
    self-loop attempts and same-version invariant violations
    if those can fire here.

Likely catches:
  - The JS module's render needs invalidation so handles re-attach
    after a re-mount. Currently mount() rebuilds the SVG from scratch,
    so handles get re-created — good.
  - Drag from a handle reaches both the canvas's `drop` listener
    and the target node's listener. Pick one: probably the canvas's,
    since the JS module is the one that knows which node was under
    the cursor (via event.target / `closest()`). The palette drop
    listener stays — it distinguishes palette vs edge drags by
    inspecting `dataTransfer.types`.

No new repo work (Chunk 3 already shipped the wiring methods).
The new tests, if any, live alongside the JS module — but per the
established pattern, JS interop is exercised manually on the dev
machine, not unit-tested. The repository tests already cover all
the renumber cases.

PAT note: each session, user provides a short-lived PAT for the repo.
