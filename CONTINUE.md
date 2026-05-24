# CONTINUE.md

> Session handoff. Read this first when picking the project back up.

## Where we are

**Phase 5 in progress.** Chunks 1-7 and 9 done (Chunk 8 deferred
pending user review of need). Chunk 7 was the design pivot from
Chunk 6's "free drop palette" model to the +-button graph
construction model; Chunk 9 added the matching deletion surface:

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
    FK); the UI just doesn't create them. Old workflows from the
    Chunk 6 surface that have orphans are dev artifacts.
  - **Schema follow-up**: `CK_workflow_nodes_decision_both_edges`
    dropped (a Decision used to require both path FKs at insert
    time). Chunk 10's promotion gate is now the sole enforcer
    of "every Decision has both children." Defense-in-depth at
    the data layer is gone for this rule; the cost was a hard
    block on the new model, since Decisions necessarily exist
    with one or zero children mid-design.
  - **Chunk 9** added X (delete) buttons in the top-left of every
    non-Start node, plus a confirmation dialog that branches:
      - Terminal or childless Process: plain confirm.
      - Process with descendants: two buttons — splice into
        parent (`DeleteAndSpliceAsync` lifts the single child up
        one level, renumbers the surviving subtree via the same
        CTE) or delete subtree (`DeleteSubtreeAsync` recursively
        removes the node and all descendants).
      - Decision: single destructive button, no splice option
        (two subtrees can't cleanly merge). Both-subtree count
        surfaced in the confirm copy.
    Start delete is blocked — the X button isn't even rendered
    on Start.

Test surface: 158 → 194 (+1 workflow def repo, +19 node repo
Chunk 7, +15 node repo Chunk 9, +1 regression test for the
two-batch-CTE bug fix below).

  - **Chunk 9 bug fix**: `DeleteSubtreeAsync` had a data-loss
    bug — deleting a Decision left its children behind as
    orphans. Root cause: the original implementation ran two
    separate `ExecuteAsync` calls with the same recursive CTE
    (one to null intra-subtree FKs, one to DELETE). The first
    statement nulled the FKs the second's CTE walk depended on,
    so the second walk found only the seed and deleted only one
    row. Fix: materialize subtree IDs into a `@ids` table
    variable in one batch up front, then reference it for both
    operations. See LessonsLearned entry 20.

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
- `LessonsLearned.md` — twenty entries.
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

**Phase 5 / Chunk 10 — Promotion-time validation + end-of-phase rollup.**

The version's `Draft → InService` transition
(`RequestTypeVersionRepository.TransitionToInServiceAsync` from
Phase 4) must refuse promotion when any workflow on the version
has a malformed graph: every non-terminal node must have its
required children present.

This is **load-bearing** because the schema CHECK that used to
enforce "every Decision has both path1 and path2 set" was dropped
in Chunk 7's bug-fix follow-up. Defense-in-depth is gone at the
data layer; the promotion gate is now the sole enforcer.

Concretely:
  - SQL pre-check in `TransitionToInServiceAsync`: count
    non-terminal nodes with missing required children. The rules:
      - Decision: needs both path1 and path2.
      - Start, Process: needs path1.
      - Terminals: no children.
    If any → return a new outcome enum value
    (`RejectedIncompleteWorkflow` or similar) with the offending
    workflow names attached, or just the count for a generic
    "X workflows have incomplete graphs" message.
  - Inline incomplete-badge rendering on the canvas (the
    designer-page side): the JS module shows a small yellow !
    on any node missing its required children. Helps the user
    fix things before trying to promote. The dashed-edge-to-a-+
    pattern from Chunk 7's polish already conveys this visually
    for empty slots; the badge would be a separate cue for nodes
    in an already-incomplete state (e.g. a Decision with only
    one branch where the user might miss the dashed edge in a
    busy canvas).
  - End-of-phase rollup:
      - Update PLAN.md to reflect what actually shipped vs the
        provisional Phase 5 chunk list.
      - CONCEPT.md final polish.
      - Remove the temporary node-list readout table below the
        canvas. The visual is enough now that delete and insert
        both work end-to-end.

**Chunk 8 — Node property editor (deferred).**

Deferred at user request pending review of whether it's needed
for Phase 5 close-out. Node property editor would be the side
panel that lets users set prompt_text, approver_group_id,
stale_threshold_days, stale_message_text, notes on individual
nodes. `UpdateAsync` already shipped in Chunk 3 and handles all
these fields, so the repo work is done; what remains is purely
UI:
  - Body-click handler on each node `<g>` in the JS module.
  - `[JSInvokable] OnNodeClickedAsync(nodeId)` opens a side panel.
  - Field shape depends on node type (Decision shows path1/path2
    prompt; non-Decision shows only prompt).
  - Auto-save on blur (locked design).
  - Selected-node visual highlight on the canvas.

If we decide we don't need it for v1, the editable fields stay
at their defaults until someone manually edits the DB rows. The
workflow engine (Phase 6+) will run either way.

**Chunk 9 — Delete affordances (DONE, see Where We Are).**

PAT note: each session, user provides a short-lived PAT for the repo.
