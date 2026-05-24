# CONTINUE.md

> Session handoff. Read this first when picking the project back up.

## Where we are

**Phase 5 in progress.** Chunks 1-7 done. Major design shift in
Chunk 7 (the previous "free drop palette" model from Chunk 6 was
superseded by a +-button graph-construction model):

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

Test surface: 158 → 178 (+1 workflow def repo, +19 node repo).

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
- `LessonsLearned.md` — sixteen entries.
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

**Phase 5 / Chunk 8 — Node property editor.**

Side panel that opens when the user clicks a node body (not a +
button). Reads the node's editable properties and lets the user
update them. Properties on `workflow_node` per the schema:
  - prompt_text, path1_prompt_text, path2_prompt_text
  - approver_group_id (FK to approver_groups, when seeded)
  - stale_threshold_days, stale_message_text
  - notes

Scope:
  - JS module: add a body-click handler on each node `<g>`. On
    click, call a new `[JSInvokable] OnNodeClickedAsync(nodeId)`.
    Distinguish from + clicks by stopping propagation on the +
    button (already done in Chunk 7).
  - Razor: a `MudDrawer` or `MudCard` panel on the right side
    that becomes visible when a node is selected. Loads
    `_selectedNode` via `NodeRepository.GetByIdAsync(nodeId)`.
    Fields per the node's type (Decision shows path1/path2
    prompt text; non-Decision shows only prompt_text). Approver
    group dropdown if the seeded `approver_groups` table has
    rows (otherwise hide).
  - Auto-save on blur per the locked design: each field commits
    via `UpdateAsync` when focus leaves. Snackbar on save.
  - Visual cue on the canvas: the selected node gets a thicker
    stroke or a glow. JS module reads a `selectedNodeId` from
    the graph data payload to know which node to highlight.

No schema changes. `UpdateAsync` already shipped in Chunk 3 and
handles all the property fields.

**Chunk 9 — Delete affordances.**

Per the design conversation: terminal delete is confirm-only,
Process delete asks "keep descendants — splice into parent" or
"delete this and N descendants," Decision delete is a single
warning ("Delete this Decision and both subtrees, N total
descendants?" — no splice option). Start delete is blocked.

New repo methods needed:
  - `DeleteSubtreeAsync(rootNodeId)`: recursive CTE to find all
    descendants reachable via path1/path2; null upstream parent
    FK; delete the subtree in dependency order; delete root.
  - `DeleteAndSpliceAsync(nodeId)`: only valid for single-child
    nodes (Start, Process). Read the node's path1 (= surviving
    child). Update parent's pointer to skip the deleted node.
    Delete the node. Renumber the surviving subtree (shift up
    by 1) via the existing `RenumberSubtreeAsync` CTE.

The existing `DeleteAsync` from Chunk 3 stays as the leaf-only
primitive (no splice, no subtree). Tests for it still apply.

**Chunk 10 — Promotion-time validation.**

The version's `Draft → InService` transition
(`RequestTypeVersionRepository.TransitionToInServiceAsync` from
Phase 4) must refuse promotion when any workflow on the version
has a malformed graph: every non-terminal node must have its
required children present.

Required because the schema CHECK that used to enforce
"every Decision has both path1 and path2 set" was dropped in
Chunk 7's bug-fix follow-up. Defense-in-depth is gone at the
data layer; the promotion gate is now the sole enforcer.

Concretely:
  - SQL pre-check in `TransitionToInServiceAsync`: count
    Decisions with NULL path1 or path2, AND count Start/Process
    nodes with NULL path1 (Decision is excluded because Decision
    is a per-slot rule). If any → return a new outcome enum
    value (`RejectedIncompleteWorkflow` or similar) with the
    offending workflow names attached.
  - Inline incomplete-badge rendering on the canvas (the
    designer-page side): the JS module shows a small yellow !
    on any node missing its required children. Helps the user
    fix things before trying to promote.
  - End-of-phase rollup: PLAN doc, CONCEPT polish, ad-hoc
    cleanup of any temp UI affordances (the node-list readout
    table below the canvas can probably go in this chunk).

PAT note: each session, user provides a short-lived PAT for the repo.
