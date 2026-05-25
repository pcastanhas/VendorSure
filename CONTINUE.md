# CONTINUE.md

> Session handoff. Read this first when picking the project back up.

## Where we are

**Phase 5 complete.** Chunks 1-7, 9, and 10 shipped, followed by an
extensive post-Chunk-10 cleanup pass. Chunk 8 (node property editor)
remains deferred — `WorkflowNodeRepository.UpdateAsync` handles all
the property fields, so the only missing piece is a side-panel UI.

The Phase 5 build sequence:

- **Chunks 1-4** — Workflow definition / node / block catalog repos
  plus the designer page shell.
- **Chunk 5** — D3 interop spike. Pure-SVG canvas via D3 loaded from
  a vendored npm dep. JS module renders the graph; Blazor owns the data.
- **Chunk 6** — Palette + drag-to-add nodes. Effectively reverted by
  Chunk 7's design pivot.
- **Chunk 7 — Design pivot to +-button graph construction.** Every
  workflow auto-creates a Start node in the workflow create
  transaction. JS draws + buttons on every non-terminal node's open
  slots; clicking + opens a block-picker dialog.
  `IWorkflowNodeRepository.InsertChildAsync` atomically inserts +
  wires + renumbers. Followed by a schema follow-up that dropped
  `CK_workflow_nodes_decision_both_edges` (move "Decision has both
  children" from edit-time to promotion-time).
- **Chunk 9 — Delete affordances.** X (delete) button on every
  non-Start node. Confirmation dialog branches on node type and
  descendant count: terminal/childless-Process is plain confirm;
  Process with descendants offers splice-into-parent
  (`DeleteAndSpliceAsync` lifts single child up + renumbers) or
  delete-subtree (`DeleteSubtreeAsync` recursive cascade); Decision
  offers subtree-delete only; Start delete is blocked. Plus a bug
  fix for the recursive CTE destroying its own walk data across two
  ExecuteAsync calls. See LessonsLearned entry 20.
- **Chunk 10 — Promotion-time validation + end-of-phase rollup.**
  `TransitionToInServiceAsync` now refuses `Draft → InService` when
  any workflow on the version has structural issues (non-terminal
  node missing required children, orphan node, no Start).
  `TransitionToInServiceResult` is now a record with `Outcome` +
  `Issues` list; UI surfaces issues in a sticky snackbar.

**Post-Chunk-10 cleanup pass (this session):**

A sustained polish pass turning the chunk-10-complete state into a
release-quality workflow designer plus a new admin Blocks page. The
core threads:

- **`block_catalog` schema enrichment.** Added `name nvarchar(50)`
  (short label for picker + canvas), `path1_decision` /
  `path2_decision` (block-level branch labels for Decision blocks,
  enforced by `CK_block_catalog_decision_labels`), and `actor_type`
  (int enum: 1=System, 2=Human, 3=AI, enforced by
  `CK_block_catalog_actor_type`). Each landed with backfill SQL in
  the commit message and matching domain/repo/test changes.
- **Dropped dead columns.** `workflow_nodes.path1_prompt_text` and
  `path2_prompt_text` were the wrong layer once labels moved to
  `block_catalog`. Removed from schema, domain, repo, and
  `IWorkflowNodeRepository.UpdateAsync`.
- **Canvas rendering polish.** Designer route now navigates to the
  designer on workflow create (was returning to Workflows tab).
  X-button moved to top-center (was top-left, blocking long block
  names). Node ID labels (`#nn`) removed. Per-block color from
  `block_catalog.color` rendered with darkened-fill-derived stroke.
  Decision diamonds darkened to orange-800 with white text for
  consistency with other node types. Block-level path labels
  rendered on diamond outgoing edges in neutral muted grey (no
  red/green coding — see LessonsLearned 23). Actor-type icon
  (gear/person/robot SVG) prefixed to each block-bearing node's
  label. Native `<title>` tooltip replaced by a custom MudBlazor-
  styled multi-line hover overlay (immediate appearance, actor icon
  + bold name on line 1, description on line 2, cursor-following).
- **Admin Blocks page** at `/admin/blocks`. Two tables (Process /
  Decision), color swatches, active toggle column, edit pencil.
  Edit dialog with 4-swatch color picker per node type, actor radio,
  conditional Decision-label fields, class_name locked from editing
  on blocks referenced by any workflow_node. Repo grew authoring
  methods to support this (`ListAllAsync`, `GetByIdAsync`,
  `CreateAsync`, `UpdateAsync`, `SetActiveAsync`,
  `CountWorkflowNodeReferencesAsync`) with new outcome enums.
  Two-layer enforcement of the class_name-change rule: UI disables
  the field when refs > 0; repo refuses with
  `RejectedClassNameChangeBlocked` if a concurrent caller slips
  through.
- **Bug fix.** `UpdateAsync` was tripping the in-use-block check on
  benign whitespace differences (dialog's `.Trim()` on save against
  a stored value with trailing whitespace). Both sides now trimmed
  before the equality check.

**Test surface: 158 → 220.** Phase 5 baseline was 201 after Chunk 10;
the cleanup pass added 19 more (mostly the new block-catalog repo
methods) to reach 220.

Phase 5 design (locked in by the end of the cleanup):
  - **D3.js** for the SVG canvas. No React, no build pipeline.
  - **Fixed layout** computed from `execution_level` (vertical row)
    + parent-driven horizontal slots (path1 = left, path2 = right).
  - **Graph rooted at Start** — every workflow has one and only one;
    auto-created on workflow create.
  - **Structural validity via UI affordances** — schema CHECKs are
    safety net; UI doesn't let users produce invalid graphs.
  - **Promotion-time validation** for Draft → InService.
  - **`execution_level`** = topological depth; designer renumbers
    downstream nodes on insert/delete.
  - **Branch merging deferred** — schema permits, editor refuses.
  - **Block-level semantics.** A `block_catalog` row precodes the
    actor (System/Human/AI), the predicate (for Decisions), and
    the meanings of path1/path2 (for Decisions). The workflow author
    picks blocks; they don't author block internals. Block authoring
    is now admin-UI-driven (`/admin/blocks`) but block .NET classes
    are still hand-coded.
  - **Auto-save per atomic edit.** Each insert/delete/edit commits
    its own transaction.

Read these to get oriented:
- `docs/PLAN.md` — the phase/chunk roadmap. Phase 5 is done;
  next is Phase 6. PLAN's Phase 5 section reflects what shipped
  (the provisional chunk list was superseded by the design
  conversation, captured both there and in commit messages).
- `docs/data-model.sql` — the reviewed schema.
- `docs/CONCEPT.md` — design intent. The workflow designer section
  was refreshed multiple times across Phase 5 and is current as of
  the cleanup. §3.1 and §3.2 still scheduled for refresh in
  Phase 6 / Phase 9.
- `BUILD.md` — how to build/run locally. "What's currently built
  (Phases 1-5)" summarises the shipped surface.
- `LessonsLearned.md` — twenty-three entries.
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

**Phase 5 is complete.** Next is **Phase 6**, now split into three
sub-phases to keep each session's work inside the token budget:

- **6A — Storage.** One chunk: `IDocumentStorage` +
  `LocalDiskDocumentStorage`.
- **6B — AI Service + Validation Runner.** Two chunks: 6B.1 AI Service
  (with throwaway `/test/ai` page), 6B.2 Validation Runner (with
  throwaway `/test/runner` page).
- **6C — Submission Portal.** Four chunks: pick-type, upload, results,
  re-submit. Removes both test pages.

Doc commit at the end of *each* sub-phase. CONCEPT.md §3.1 + §3.2
refresh lands at the 6C doc commit.

The Phase 5 Chunk 8 carry-over (node property editor) was reviewed
and **dropped as stale** at the start of the 6A session — the engine
does not in fact need it for Phase 6, and if/when per-node prompts
are needed for Phase 7 the side panel can be scoped fresh against
the actual engine requirements.

Read the Phase 6 section in `docs/PLAN.md` for the full chunk-level
detail before starting code on 6A.

### Top of the TODO list for next session

In rough priority order:

1. **Ship Phase 6A — Storage.** Single chunk. See PLAN.md Phase 6A.
   `IDocumentStorage` interface in Services, `LocalDiskDocumentStorage`
   impl in Infrastructure, unit tests against a temp directory,
   integration test against the NAS path if available. Then 6A doc
   commit (standard files; no CONCEPT.md changes expected).

2. **Wire CI.** Tests run on the developer's machine via
   `dotnet test`. A buggy Chunk 9 commit shipped because the test
   that should have caught the bug existed but apparently wasn't
   executed before commit. Wiring a minimal GitHub Action that runs
   `dotnet build` + `dotnet test` on push would prevent that class
   of failure. Infrastructure tests require a live DB, so CI also
   needs a SQL Server target (LocalDB on a Windows runner?
   containerized SQL? Azure SQL test instance?) — scope this when
   we get to it.

### Carried-forward items, not blocking

**Inline incomplete-node badges.** Skipped in Chunk 10 because the
dashed-dangling-edge cues from Chunk 7 are visible enough at our
scale. If a busier-canvas workflow shows up where the eye can't
easily spot dashed lines, add the yellow `!` badge then.

**Dev DB cleanup.** Pre-Chunk-7 workflows may have leftover orphan
nodes that the new promotion gate will refuse. One-time cleanup
when needed:

```sql
-- Find them
SELECT n.id, n.workflow_definition_id, n.node_type_id, n.execution_level
FROM dbo.workflow_nodes n
INNER JOIN dbo.workflow_definitions wd ON wd.id = n.workflow_definition_id
WHERE n.id <> ISNULL(wd.start_node_id, -1)
  AND NOT EXISTS (
    SELECT 1 FROM dbo.workflow_nodes p
    WHERE p.workflow_definition_id = n.workflow_definition_id
      AND (p.path1_node_id = n.id OR p.path2_node_id = n.id)
  );

-- Delete them (verify the SELECT first):
DELETE n
FROM dbo.workflow_nodes n
INNER JOIN dbo.workflow_definitions wd ON wd.id = n.workflow_definition_id
WHERE n.id <> ISNULL(wd.start_node_id, -1)
  AND NOT EXISTS (
    SELECT 1 FROM dbo.workflow_nodes p
    WHERE p.workflow_definition_id = n.workflow_definition_id
      AND (p.path1_node_id = n.id OR p.path2_node_id = n.id)
  );
```

**Whitespace-padded class_name backfill (optional).** During the
cleanup pass we fixed `UpdateAsync` to ignore whitespace-only
differences in `block_catalog.class_name`. If any rows still have
trailing whitespace from legacy data, one editing pass through the
admin UI fixes them automatically; bulk cleanup is also fine:

```sql
UPDATE dbo.block_catalog
SET class_name = LTRIM(RTRIM(class_name))
WHERE class_name <> LTRIM(RTRIM(class_name));
```

PAT note: each session, user provides a short-lived PAT for the repo.
