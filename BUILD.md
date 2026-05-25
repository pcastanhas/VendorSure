# BUILD.md

> How to build, run, and test VendorSure locally.

## Prerequisites

- **.NET 10 SDK** (10.0.107 or later)
- A **SQL Server** reachable from your dev machine. Local Express, remote
  dev box, or container — all fine. The DB name should be `VenSure` to
  match what `data-model.sql` expects.
- The `VenSure` database created and `docs/data-model.sql` applied
  against it. The app pings the DB at startup and queries the `users`
  table on the first request; both need to work for the app to come up
  signed in.

## What's currently built (Phases 1-5, 6A)

End-to-end surface as of the Phase 5 rollup:

**Foundation (Phase 1):**

- Five `src` projects, five matching test projects, MudBlazor 9.4 shell.
- Serilog file logging (`logs/app-YYYY-MM-DD.log`, daily rolling,
  30-day retention) and console output.
- DB connection factory + reachability check at startup; app boots even
  if the DB is unreachable so the operator can fix configuration.
- Debug identity shim (configured via `Debug:Identity` in
  `appsettings.json`) — every request is authenticated as the
  configured `users` row. Tagged `REMOVE-BEFORE-PROD` throughout. The
  shim refuses to load when `ASPNETCORE_ENVIRONMENT=Production`.
- Dapper repository pattern (SELECT-with-explicit-`AS PascalCase`
  aliases) — established here for everything that follows.
- Settings admin page at `/admin/settings` — read and edit values for
  the rows seeded in `data-model.sql` §16. Sensitive values masked in
  the list, reveal-toggle in the edit dialog, required-validation on
  save.

**Identity admin (Phase 2):**

- User Groups admin page at `/admin/user-groups` — list/create/edit
  groups with permission flags. The IsActive switch is disabled when
  the group has users assigned (rule enforced both in the UI and in
  repository SQL).
- Users admin page at `/admin/users` — list/create/edit users with
  their entraid, group assignment, admin flag, active flag. Group
  picker filters to active groups only. Distinct snackbar messages for
  entraid collisions and inactive-group rejection (race-condition
  fallbacks).
- The "Local Dev" seed user and "Developers" seed group from setup
  step 3 below now appear in the admin UI; you can edit them through
  the pages instead of running SQL.

**Document catalog admin (Phase 3):**

- Required Documents admin page at `/admin/required-documents` —
  list/create/edit/delete the catalog of document types Request Types
  can later require. First admin page with a hard delete: confirmation
  via `ShowMessageBoxAsync`, repository rejects deletion of any row
  referenced by a Request Type version. Snackbar maps each outcome
  (Deleted, NotFound, RejectedReferenced) distinctly.

**Request Type editor (Phase 4):**

- Request Types list page at `/admin/request-types` — one row per
  type with chip-styled version indicators for In Service / Draft /
  Superseded count. "New request type" creates the type and its
  initial Draft v1 atomically (single SQL transaction). Open-in-new
  icon navigates to the detail page.
- Request Type detail page at `/admin/request-types/{id}` — three
  vertically-stacked sections:
  1. **Type-level edit** — Name, IsActive, IsExplanationRequired with
     Save button. Type-level fields apply across all versions and are
     always editable (immutability is a per-version rule, not per-type).
  2. **Version selector + metadata + transition buttons** — dropdown
     of versions; Created / Placed in service / Superseded timestamps;
     "Place v{N} in service" button (Draft only) and "Create new
     Draft" button (when no Draft exists). The transition buttons are
     the visible face of `TransitionToInServiceAsync` and
     `CreateDraftAsync`.
  3. **Four tabs** — Workflows, Required Documents, Validations,
     Selection Prompt.
- **Required Documents tab** — table of library entries × attachment
  state per version. Toggle attached/required; read-only when the
  displayed version isn't Draft.
- **Validations tab** — table of validations in execution order with
  Add / Edit / Delete and a Documents sub-picker per validation.
  The sub-picker is scoped to the version's currently-attached
  required documents because the validation-document junction enforces
  same-version (the schema FKs only enforce existence).
- **Selection Prompt tab** — single multi-line textarea bound to the
  version's `workflow_selection_prompt`; Save enabled only when dirty.
- **State transitions** — placing a Draft in service is transactional:
  the prior In Service version (if any) is demoted to Superseded with
  `superseded_ts` set, and the new version is promoted with
  `placed_in_service_ts` set, both using the same DateTime value so
  the audit timestamps line up exactly. UPDLOCK on the initial Draft
  check serialises concurrent promotion attempts. The transition now
  also runs structural validation across every workflow on the version
  (added in Phase 5 / Chunk 10) — promotion is refused if any workflow
  has a non-terminal node missing required children, an orphan node,
  or no Start. Rejection surfaces the first few issues in a sticky
  snackbar.

**Workflow Designer (Phase 5):**

- **Workflows tab** on the Request Type detail page lists the
  workflows on the current version. New / Edit / Delete in-place
  on Draft versions; read-only on Superseded / In Service.
- **Workflow Designer page** at
  `/admin/request-types/{typeId}/workflows/{workflowId}/designer` —
  pure-SVG canvas via D3 in a vendored JS module
  (`wwwroot/js/workflow-designer.js`), Blazor owns the data.
  Created workflows auto-receive a Start node; the user grows the
  graph from Start by clicking + buttons on every non-terminal
  node's open slots. + opens a block picker dialog filtered by the
  slot's allowed node types; selecting a block calls
  `IWorkflowNodeRepository.InsertChildAsync` which atomically
  inserts the node, wires the parent edge, and renumbers
  downstream `execution_level` via a recursive CTE.
- **Node body** shows the block's name (from `block_catalog.name`)
  with a small actor-type icon prefix (gear/person/robot for
  System/Human/AI). Hover shows a multi-line tooltip with the
  block's description. Per-block color override via
  `block_catalog.color` honored; otherwise node-type defaults
  apply (Process blue, Decision orange).
- **Decision diamonds** carry path1/path2 labels lifted from
  `block_catalog.path1_decision` / `path2_decision` — block-level
  semantics rendered on the horizontal edges leaving each vertex
  in neutral muted grey.
- **X delete button** in the top middle of every non-Start node
  opens a confirmation dialog that branches on node type and
  descendant count: terminal/childless-Process is plain confirm;
  Process with descendants offers splice-into-parent
  (`DeleteAndSpliceAsync`) or delete-subtree (`DeleteSubtreeAsync`);
  Decision offers subtree-delete only.
- **Admin Blocks page** at `/admin/blocks` — admin UI over
  `block_catalog`. Two tables (Process / Decision), each with a
  color swatch, name, description, actor, path labels (Decision
  only), and active toggle. Blocks are never deleted, only
  deactivated. Edit dialog uses a 4-swatch color picker per node
  type. Class name is locked from editing on blocks that are
  referenced by any workflow_node (admin must deactivate and
  create a new block with the new class). Repo enforces the same
  rule (`UpdateBlockCatalogOutcome.RejectedClassNameChangeBlocked`).

**Document storage (Phase 6A):**

- `IDocumentStorage` abstraction in `VendorSure.Services.Documents`
  with three operations: `StoreAsync`, `RetrieveAsync`,
  `DeleteAllForRequestAsync`.
- `LocalDiskDocumentStorage` impl in `VendorSure.Infrastructure.Documents`.
  Writes files under `{Storage.BasePath}/{requestId}/{fileName}`.
  Settings are re-read on every call so admin-panel edits take
  effect immediately.
- Two upload guardrails enforced at store time, before bytes hit
  disk and before any AI call:
  - `Storage.AllowedFileExtensions` — comma-separated allow-list
    (seeded as `pdf,jpg,jpeg,png,gif,webp,txt`, matching what
    Anthropic's API can process directly).
  - `Storage.MaxFileSizeBytes` — per-file size cap (seeded at
    10 MB).
- User-facing rejections (disallowed extension, oversized file)
  surface as `StoreDocumentOutcome` values for the caller to
  render. Programmer-error / hostile-input filename violations
  (path separators, `..`, null bytes, empty, >200 chars) throw
  `InvalidDocumentFileNameException`.
- No UI yet — the storage abstraction will be exercised by the
  validation runner (6B.2) and the submission portal (6C).

## First-time setup

1. Clone the repo.

2. Copy `src/VendorSure.UI/appsettings.example.json` →
   `src/VendorSure.UI/appsettings.json` and fill in the connection string
   under `ConnectionStrings.VenSure`. Typical local-dev forms:

   ```
   Server=localhost;Database=VenSure;Trusted_Connection=True;TrustServerCertificate=True;
   Server=localhost\SQLEXPRESS;Database=VenSure;Trusted_Connection=True;TrustServerCertificate=True;
   Server=devsqlbox.mycorp.com;Database=VenSure;User Id=sa;Password=...;TrustServerCertificate=True;
   ```

   The `TrustServerCertificate=True` part is needed for local SQL Server
   installs that use a self-signed cert. For a corporate dev server with a
   proper cert, you can drop it.

3. **Seed a user for the debug identity shim.** The shim authenticates every
   request as the user whose `entraid` matches the configured OID, so you
   need at least one row in `users` (and the `user_groups` row it FKs to):

   ```sql
   INSERT INTO dbo.user_groups (name, is_active, can_restart_workflow, can_change_workflow, can_submit_requests)
   VALUES ('Developers', 1, 1, 1, 1);

   INSERT INTO dbo.users (entraid, name, group_id, is_admin, is_active)
   VALUES (
       '00000000-0000-0000-0000-000000000001',
       'Local Dev',
       (SELECT id FROM dbo.user_groups WHERE name = 'Developers'),
       1, 1);
   ```

   The OID in `appsettings.example.json` is `00000000-0000-0000-0000-000000000001`.
   If you change it in `appsettings.json`, change the INSERT to match.

4. Restore packages and build:

   ```
   dotnet build VendorSure.slnx
   ```

   First restore takes a minute or two.

## Run

```
dotnet run --project src/VendorSure.UI
```

Browse to the URL the launcher prints (typically `https://localhost:7298`).
You should see a MudBlazor shell: top app bar reading "VendorSure" on the
left and "Local Dev" (or whatever name you seeded) on the right, a left
nav drawer with menu items (Home, Settings, Users, etc.), and the home
page in the content area.

If the top bar shows "Not signed in" in orange instead of a user name, the
debug identity shim failed to find a user — check the log file for the
specific error (typically: no `users` row with the configured `entraid`,
or `Debug.Identity.Enabled` is false in `appsettings.json`).

## Test

```
dotnet test VendorSure.slnx
```

### Integration tests against the dev DB

`VendorSure.Infrastructure.Tests` runs against the same dev SQL Server the
app uses. Before the first run:

1. Copy `tests/VendorSure.Infrastructure.Tests/appsettings.Test.example.json`
   → `tests/VendorSure.Infrastructure.Tests/appsettings.Test.json` and fill
   in the connection string. The file is gitignored.

2. Make sure the `VenSure` database has the §16 settings rows from
   `data-model.sql`. The tests assume the seeded set is present.

Tests modify rows in `try/finally` blocks and restore the original state.
If a test crashes mid-flight some cleanup may be needed:

- **Settings tests** use one seeded row (`AI.Polling.IntervalMinutes`,
  original value `5`) as a probe and restore its value in `finally`. If
  this gets left modified, re-run the §16 INSERT from `data-model.sql`
  or fix the key by hand.
- **UserGroup + User tests** create fresh rows in `dbo.user_groups`
  and `dbo.users` with `_test_`-prefixed names and hard-delete them in
  `finally`. Stray rows are harmless leftovers.
- **DocumentType tests** create rows in `dbo.required_documents_library`,
  and the delete-rejection test also stands up rows in `dbo.request_types`,
  `dbo.request_type_versions`, and `dbo.request_type_required_documents`.
  All `_test_`-prefixed; FK order matters for cleanup.
- **Request Type / Version / Junction / Validation tests** create rows
  in `dbo.request_types`, `dbo.request_type_versions`,
  `dbo.request_type_required_documents`, `dbo.request_type_validations`,
  and `dbo.request_type_validation_documents`. All `_test_`-prefixed.
  Idempotent `IAsyncDisposable` fixture handles cleanup in FK order;
  if a test fixture itself crashes the cleanup query below covers it.

Cleanup query if needed (run in FK-safe order):

  ```sql
  DELETE FROM dbo.request_type_validation_documents
   WHERE request_type_validation_id IN (
       SELECT v.id FROM dbo.request_type_validations v
       INNER JOIN dbo.request_type_versions ver ON ver.id = v.request_type_version_id
       INNER JOIN dbo.request_types t           ON t.id = ver.request_type_id
       WHERE t.name LIKE '_test_%');
  DELETE FROM dbo.request_type_validations
   WHERE request_type_version_id IN (
       SELECT id FROM dbo.request_type_versions
        WHERE request_type_id IN (SELECT id FROM dbo.request_types WHERE name LIKE '_test_%'));
  DELETE FROM dbo.request_type_required_documents
   WHERE required_document_library_id IN
       (SELECT id FROM dbo.required_documents_library WHERE name LIKE '_test_%');
  DELETE FROM dbo.request_type_versions
   WHERE request_type_id IN
       (SELECT id FROM dbo.request_types WHERE name LIKE '_test_%');
  DELETE FROM dbo.request_types              WHERE name    LIKE '_test_%';
  DELETE FROM dbo.required_documents_library WHERE name    LIKE '_test_%';
  DELETE FROM dbo.users                      WHERE entraid LIKE '_test_%';
  DELETE FROM dbo.user_groups                WHERE name    LIKE '_test_%';
  ```

## Solution layout

```
src/
  VendorSure.Domain/             entities, enums, value objects
  VendorSure.Services/           orchestration + interfaces it consumes
  VendorSure.Infrastructure/     Dapper data access, storage, Claude client
  VendorSure.BackgroundWorkers/  Windows Service host (workflow engine,
                                 budget polling) — placeholder worker for
                                 now
  VendorSure.UI/                 Blazor Server host (MudBlazor)
tests/
  one project per src/ project
```

Dependency direction: `UI` and `BackgroundWorkers` → `Services` →
`Infrastructure` → `Domain`. `Domain` references nothing.

## Logs

Serilog writes to `logs/app-YYYY-MM-DD.log` in the working directory (next
to the `VendorSure.UI` binary at runtime). Daily rolling, 30-day retention.
The console sink also writes to stdout while the app is in the foreground.

Sample startup banner you should see in the log (order is approximate —
hosted services and per-request auth happen at different points so lines
may interleave):

```
[HH:MM:SS INF] VendorSure UI starting up
[HH:MM:SS INF] VendorSure UI ready — environment Development
[HH:MM:SS INF] Connected to VenSure database (server=..., database=VenSure)
[HH:MM:SS INF] Debug identity shim authenticated as 'Local Dev' (entraid=...). REMOVE-BEFORE-PROD.
```

The "Debug identity shim authenticated" line appears on the first request
that triggers the lookup, not at host startup. You'll see it once per app
run (the principal is cached after the first hit).

If the connection string is missing or the database is unreachable, you'll
see an error line instead of the "Connected to VenSure" message, but the
app still boots (so the operator can read the log and fix it).

## Schema changes

`docs/data-model.sql` is the source of truth for the DB schema. Before the
app can connect cleanly, the `VenSure` database must exist on your SQL
Server and the contents of `data-model.sql` must have been applied to it
(via SSMS, sqlcmd, or your tool of choice).

When the schema changes, edit `data-model.sql` in the repo and apply the
change manually to your dev DB. There is no migration runner —
single-developer, single-dev-DB, no production deploy in scope.

## Sandbox / agent build limitations

The development sandbox (where the agent does its work) cannot reach
`api.nuget.org`. Consequence: any restore-dependent operation
(`dotnet restore`, `build`, `test`, `run`) fails in the sandbox. The agent
authors code and commits it; **the developer is the only one who can
actually compile and test.** Round-trip: agent commits → you pull, build,
test, report back → agent fixes.
