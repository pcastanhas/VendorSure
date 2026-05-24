# CONTINUE.md

> Session handoff. Read this first when picking the project back up.

## Where we are

**Phase 5 complete.** Chunks 1-7, 9, and 10 done. Chunk 8 (node
property editor) was deferred pending review of whether it's
needed — `WorkflowNodeRepository.UpdateAsync` handles all the
property fields, so the only missing piece is the side-panel UI.

Chunk 7 was the design pivot from Chunk 6's "free drop palette"
model to the +-button graph construction model; Chunk 9 added
the matching deletion surface; Chunk 10 closed Phase 5 with
promotion-time validation and end-of-phase rollup.

  - Every workflow has a Start node, auto-created in
    `WorkflowDefinitionRepository.CreateAsync` inside the same
    transaction that creates the workflow row. The workflow ⇒
    Start invariant.
  - The JS module draws + buttons on every non-terminal node's
    open slots (one for Start/Process, two for Decision).
  - Clicking + opens a block-picker dialog (centered MudDialog;
    we explored a popover anchored to the click point but
    MudBlazor 9's popover portal pattern made it fragile).
    Terminals are offered only when the slot is empty
    (insert-between is invalid for terminals — they have no
    children to take a displaced child).
  - Inserting a Decision into a non-empty slot opens an extra
    dialog: "Which side should <existing child> attach to?"
  - New repo method `IWorkflowNodeRepository.InsertChildAsync`
    atomically inserts + wires + renumbers (uses the existing
    `RenumberSubtreeAsync` recursive CTE under the hood).
  - The schema still permits orphan nodes (level=0, no upstream
    FK); the UI doesn't create them under normal operation; the
    promotion-time validator now refuses any workflow that has
    them (Chunk 10).
  - **Schema follow-up to Chunk 7**: `CK_workflow_nodes_decision_both_edges`
    dropped. Chunk 10's promotion gate is the sole enforcer of
    "every Decision has both children."
  - **Chunk 9**: X (delete) buttons in the top-left of every
    non-Start node, plus a confirmation dialog that branches:
      - Terminal or childless Process: plain confirm.
      - Process with descendants: splice into parent
        (`DeleteAndSpliceAsync` lifts the single child up one
        level, renumbers via existing CTE) or delete subtree
        (`DeleteSubtreeAsync` recursively removes the node and
        all descendants).
      - Decision: single destructive button, no splice option
        (two subtrees can't cleanly merge).
    Start delete is blocked — the X button isn't even rendered
    on Start.
  - **Chunk 9 bug fix**: `DeleteSubtreeAsync` had a data-loss
    bug — deleting a Decision left its children behind as
    orphans. Two separate `ExecuteAsync` calls with the same
    recursive CTE: the first nulled the FKs the second's walk
    depended on. Fixed by materializing subtree IDs into a `@ids`
    table variable in one batch. See LessonsLearned entry 20.
  - **Chunk 10**: promotion-time validation in
    `TransitionToInServiceAsync`. Refuses `Draft → InService`
    when any workflow on the version has:
      - a non-terminal node missing required children (Decision
        needs both branches; Start/Process need path1),
      - an orphan node (no incoming path FK, and not the
        workflow's Start),
      - a workflow with NULL start_node_id.
    `TransitionToInServiceResult` promoted from enum to record
    with `Outcome` + `Issues` list; UI surfaces the first three
    issues in a sticky snackbar. End-of-phase rollup: removed the
    temporary node-list readout below the canvas, updated
    PLAN.md to reflect what actually shipped vs the provisional
    chunk list, polished CONCEPT.md. Inline incomplete-node
    badges deferred — the dashed dangling-edge cues from Chunk 7
    are sufficient at our scale.

Test surface: 158 → 201 (+1 workflow def repo, +19 node repo
Chunk 7, +15 node repo Chunk 9, +1 regression test for Chunk 9
bug fix, +7 version repo Chunk 10).

Phase 5 design settled before code (post-Chunk-7 shift):
  - **D3.js** for the SVG canvas. No React, no build pipeline,
    one npm dep vendored locally.
  - **Fixed layout** computed from `execution_level` (vertical row)
    + parent-driven horizontal slots (path1 = left, path2 = right
    consistently). No `x`/`y` columns in the schema.
  - **Graph rooted at Start** — every workflow has one and only
    one Start, auto-created on workflow create. Graph grows from
    Start via + buttons; no orphan nodes by construction.
  - **Structural validity via UI affordances** — the schema CHECKs
    are the safety net, but the UI doesn't let the user produce
    structurally-invalid graphs in the first place.
  - **Promotion-time validation** — Draft can hold half-wired
    workflows; the `Draft → InService` transition refuses
    workflows where any non-terminal node is missing required
    children. (Implements Chunk 10.)
  - **`execution_level`** = topological depth. Designer renumbers
    downstream nodes on insert/delete; engine walks levels in
    Phase 6+.
  - **Branch merging deferred** — schema permits, editor refuses.
  - **Workflows tab → list page** on the Request Type detail page.
  - **Designer opens on a separate route**
    `/admin/request-types/{typeId}/workflows/{workflowId}/designer`.
  - **Auto-save per atomic edit.** Each insert/delete commits its
    own transaction.
  - **Block + artifact catalog seeded manually on dev DB** by the
    user. Phase 5 code reads them as-is.

Read these to get oriented:
- `docs/PLAN.md` — the phase/chunk roadmap. **Next step is Phase 5
  / Chunk 8 — node property editor.** PLAN's provisional Phase 5
  chunk list was superseded by the design conversation; the
  locked-in plan lives in the chat transcripts and on the commit
  log.
- `docs/data-model.sql` — the reviewed schema.
- `docs/CONCEPT.md` — design intent. §3.3 covers Settings, User Groups,
  Users, Required Documents, and the Request Type editor in detail;
  §3.1 and §3.2 still scheduled for refresh in Phase 6 / Phase 9.
- `BUILD.md` — how to build/run locally. "What's currently built
  (Phases 1-4)" summarises the shipped surface.
- `LessonsLearned.md` — twenty-two entries.
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

**Phase 5 is complete.** Next is **Phase 6 — AI Service + Storage +
Submission Portal**, per `docs/PLAN.md`. Phase 6 is also large
and the chunk list there is provisional. Read the Phase 6 section
in PLAN.md to start the design conversation before code.

A few items from Phase 5 are deliberately left open and worth
deciding on before or during early Phase 6:

**Chunk 8 — Node property editor (deferred from Phase 5).**

Side panel that lets users set prompt_text, approver_group_id,
stale_threshold_days, stale_message_text, notes on individual
nodes. The repo's `UpdateAsync` already handles all these fields;
what's missing is purely UI:
  - Body-click handler on each node `<g>` in the JS module.
  - `[JSInvokable] OnNodeClickedAsync(nodeId)` opens a side panel.
  - Field shape depends on node type (Decision shows path1/path2
    prompts; non-Decision shows only the single prompt).
  - Auto-save on blur (locked design).
  - Selected-node visual highlight on the canvas.

If we don't ship this, every node uses default values until
someone edits the DB rows directly. The workflow engine in Phase
6 still runs, but every Process prompt will be empty and every
approver group will be null. That's probably not viable for v1;
plan to pick this up early in Phase 6 if it isn't done first.

**CI not wired.** Tests run on the developer's machine via
`dotnet test`. A buggy Chunk 9 commit shipped because the test
that should have caught the bug existed but apparently wasn't
executed before commit. Wiring even a minimal GitHub Action that
runs `dotnet build` + `dotnet test` on push would prevent this
class of failure. The infrastructure tests require a live DB
connection so CI also needs a SQL Server (LocalDB? containerized
SQL? Azure SQL test instance?) — non-trivial but worth scoping.

**Inline incomplete-node badges.** Skipped in Chunk 10 because
the dashed-dangling-edge cues from Chunk 7 are visible enough at
our scale. If a busier-canvas workflow shows up where the eye
can't easily spot dashed lines, add the yellow `!` badge then.

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

PAT note: each session, user provides a short-lived PAT for the repo.
