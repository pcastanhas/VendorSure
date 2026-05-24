# VendorSure — Project Plan

> Living document. The build is broken into **phases**, each phase into **chunks**.
> A chunk is a single focused commit that leaves the app runnable and adds one
> testable thing. Plans drift; chunks get added or reshuffled as we learn. This
> doc is the running record.

## How this works

- **One commit per chunk.** Tests live with the chunk that produces the code
  being tested (where there's something meaningful to assert; pure plumbing
  chunks may have no tests).
- **No throwaway scaffolding.** Each chunk is real code that ships. When a UI
  surface is needed before the real UI is ready, the chunk adds a temporary
  test button against the real repository instead of mocking the repository.
- **Phase end = docs commit.** After the last code chunk of a phase, a docs
  commit updates `BUILD.md`, `CONTINUE.md`, `CONCEPT.md` (if affected),
  `LessonsLearned.md` (if anything was learned), and this file.
- **Session pacing.** As many chunks per session as time allows. Each chunk
  commits on its own.

## Glossary

- **Phase:** a coherent slice of the system that ends in a runnable, testable,
  documented endpoint.
- **Chunk:** the smallest unit of independent testable work within a phase.
- **Test button:** a temporary UI affordance (button, link) used during early
  chunks to exercise a repository or service from the UI before the real UI
  for that capability exists. Test buttons are removed in the chunk that
  delivers the real UI.

---

## Phase 1 — Foundation + Settings admin page ✓ COMPLETE

**Goal.** A logged-in (debug-shim) user can navigate to a Settings page, see
the seeded settings rows, and edit values. All foundation infrastructure
(solution structure, logging, DB connection, identity, MudBlazor shell) is
in place.

**Outcome:** all seven chunks shipped. The Settings admin surface works
end-to-end (list, edit-via-dialog, snackbar, refresh). Render-mode
cascade trap discovered and fixed in a post-Chunk-7 patch (see
`LessonsLearned.md`). Phase 1 rollup commits this section.

### Chunks

1. **Solution scaffold + MudBlazor empty shell.** Create the five `src` and
   five `tests` projects with correct inter-project references. Wire MudBlazor
   into `VendorSure.UI` with a minimal layout (top bar, blank content area).
   `appsettings.json` is `.gitignore`'d; commit `appsettings.example.json`
   instead. BUILD.md tells the developer to copy example → real and fill in.
   **Test:** `dotnet build` succeeds. `dotnet run` boots, page renders the
   MudBlazor shell.

2. **Serilog wiring.** Add Serilog with file sink, daily rolling, 30-day
   retention. Configuration in the example appsettings. Startup writes a
   banner line.
   **Test:** `dotnet run` produces `logs/app-YYYY-MM-DD.log` with the banner
   entry visible.

3. **DB connection factory + startup reachability check.** `IDbConnectionFactory`
   interface in Services; `SqlConnectionFactory` impl in Infrastructure using
   Microsoft.Data.SqlClient. Connection string read from appsettings via
   `IOptions<T>`. Startup runs a `SELECT 1` and logs "connected to VenSure"
   or "DB unreachable: <reason>". App still boots either way.
   **Test:** with a valid connection string, see "connected" log line; with
   a bad one, see "unreachable" log line; app still runs.

4. **Debug identity shim.** Reads `Debug.Identity.Enabled` and
   `Debug.Identity.Entraid` from `appsettings.json` (not from the `settings`
   table — that read would need the identity this shim provides). On
   request, looks up the matching `users` row by `entraid` and stamps the
   principal. Refuses to load if `Environment = Production` regardless. All
   files in `VendorSure.UI/Authentication/Debug/` tagged with
   `REMOVE-BEFORE-PROD` comments. `docs/REMOVE-BEFORE-PROD.md` updated.
   **Test:** seed one row in `users`. Configure shim. Start app. Top bar
   shows the user's name. Disable shim in config. Start app. Page shows
   "Entra not configured."

5. **Settings repository.** Dapper-based `SettingsRepository` in
   Infrastructure with `GetAllAsync()`, `GetByKeyAsync(string key)`,
   `UpdateValueAsync(string key, string? value)`. Connection management via
   the connection factory. Repository interface lives in Services.
   **Test:** `tests/VendorSure.Infrastructure.Tests/SettingsRepositoryTests`
   runs against the dev DB. Round-trips a value through update + read.

6. **Settings list page.** `/admin/settings` route. MudTable of all settings
   rows (description, value, required, sensitive). Sensitive values masked.
   Read-only for now.
   **Test:** open the page, see the 10 seeded rows, sensitive ones masked.

7. **Settings edit dialog.** Click a row → MudDialog with the value editable.
   Save commits via the repository, refreshes the list. Validation: required
   settings can't be saved with empty value.
   **Test:** edit the value of `AI.Monthly.Budget.Usd`, see new value in the
   table after save.

### Phase 1 doc commit

- `BUILD.md` initial version: prerequisites, connection string setup, debug
  shim setup, `dotnet run`, `dotnet test`, where logs go.
- `CONTINUE.md` updated to "Phase 1 complete, starting Phase 2."
- `CONCEPT.md` §3.3 gets a note that admin sections are being implemented
  starting with Settings.
- `LessonsLearned.md` initial entries (whatever surfaced).
- `PLAN.md` updated with any chunks that got added or reshuffled.

---

## Phase 2 — Users + User Groups admin pages ✓ COMPLETE

**Goal.** Admin can list, create, edit users and user groups. Group permission
flags (`can_restart_workflow`, `can_change_workflow`, `can_submit_requests`)
are editable.

**Outcome:** all four chunks shipped. Three patterns emerged worth naming
for the chunks that follow:

- **Cross-table business rules go in repository SQL.** Each rule lives
  in the WHERE clause of its mutating statement (UPDATE / conditional
  INSERT) so it's enforced atomically with no race window. When the
  rule rejects, focused existence probes disambiguate the rejection
  reason from a generic "not found." See `LessonsLearned.md` for the
  rationale and the deactivation-transition footgun caught during
  Chunk 1.
- **Result enums for expected CRUD outcomes.** Operations that have
  multiple expected outcomes (created / not-found / rejected-X /
  rejected-Y) return an enum (and for create, a small record carrying
  the new id) rather than throwing for non-exceptional cases. Each
  outcome maps to its own snackbar severity in the UI.
- **`*ListItem` projection records for list pages.** When a list view
  needs columns from a join (group name, assigned-user count), the
  repository exposes a dedicated `ListWith…Async` method returning a
  small `record (Entity, projection-data)` pair. Avoids N+1 round-trips
  and keeps Domain entities free of view-projection fields.

### Chunks

1. **UserGroup repository.** CRUD methods. Tests against dev DB.

2. **User repository — expand to CRUD.** Phase 1 / Chunk 4 added the
   `User` domain entity and `IUserRepository.GetByEntraidAsync` to
   support the debug identity shim. This chunk grows the same interface
   with the rest of the CRUD surface (list, get by id, create, update,
   deactivate) and a Dapper impl to match. Tests against dev DB.

3. **User Groups list + create + edit page.** `/admin/user-groups`. MudTable,
   dialog for new/edit, all four bit fields rendered as switches.
   **Test:** create a group, see it in the list, edit it, see the update.

4. **Users list + create + edit page.** `/admin/users`. Similar shape. Create
   user requires Entra OID, name, group selection.
   **Test:** create a user, see them in the list, edit, see the update. The
   debug shim user from Phase 1 still works.

### Phase 2 doc commit

- BUILD.md updated if any new setup step (probably not).
- CONTINUE.md updated.
- LessonsLearned.md additions if any.
- PLAN.md updated.

---

## Phase 3 — Required Documents Library ✓ COMPLETE

**Goal.** Admin can manage the catalog of document types that Request Types
will pick from.

**Outcome:** both chunks shipped. Established the first hard-delete
affordance in the admin UI, including the cross-table "rejected when
referenced" rule (same atomic SQL pattern as Phase 2's deactivation
rules) and the `MudDialogService.ShowMessageBoxAsync` confirmation
pattern. Entity named `DocumentType` rather than `RequiredDocument` —
see Chunk 1 commit for the naming rationale and namespace
(`VendorSure.Domain.Documents`).

### Chunks

1. **RequiredDocumentsLibrary repository.** CRUD.

2. **Library list + create + edit page.** Simple table, dialog, no joins.
   **Test:** create a document type, see it, edit it.

### Phase 3 doc commit

Standard.

---

## Phase 4 — Request Types (without workflow designer) ✓ COMPLETE

**Goal.** Admin can create Request Type drafts, edit their required docs,
validations, and workflow selection prompt. State transitions
(Draft → In Service → Superseded) work per the agreed rules. The Workflows
tab exists but is empty (placeholder for the designer).

**Outcome:** all nine chunks shipped. Five repositories
(`IRequestTypeRepository`, `IRequestTypeVersionRepository`,
`IRequestTypeRequiredDocumentRepository`,
`IRequestTypeValidationRepository`,
`IRequestTypeValidationDocumentRepository`) and the complete admin
editor surface (`/admin/request-types` list + `/admin/request-types/{id}`
detail page with four tabs and the two state-transition buttons).
Three durable patterns established here and worth carrying into
Phase 5 onward: **immutability enforced atomically in repository
SQL** (every mutation has a `WHERE … AND request_state = 'D'` gate, no
race window between a state-check read and the mutation); **the
"same-version invariant" via INNER JOIN** for cross-junction integrity
the schema FKs don't enforce (used for the validation-document
junction in Chunk 3); **UPDLOCK for atomic read-then-update** on the
first repo method that mutates `request_state` (Chunk 9's
`TransitionToInServiceAsync`, where two concurrent callers attempting
to promote the same Draft would otherwise race past each other and
each demote the other's not-yet-promoted row).

### Chunks

1. **RequestType + RequestTypeVersion repositories.** Hand-curated multi-
   mapping for the version → type relationship.

2. **RequestTypeRequiredDocuments junction repository.** Add/remove links to
   the library.

3. **RequestTypeValidations + ValidationDocuments repositories.** CRUD for
   validations and their per-document attachments.

4. **Request Types list page.** `/admin/request-types`. Table of types with
   their current in-service version.
   **Test:** seed two types, see them listed with version info.

5. **Request Type detail page — header tab.** Open a type → tabs across the
   top (Workflows / Required Documents / Validations / Selection Prompt).
   Header section shows name, current version, state, audit info. Editable
   only when state = Draft.
   **Test:** open a type in Draft state, edit name, save.

6. **Required Documents tab.** Multi-select picker against the library.
   **Test:** attach two docs to a Draft Request Type version.

7. **Validations tab.** List of validations (description, prompt, exec order),
   add/edit dialog. Per-validation document attachment is its own sub-picker
   inside the edit dialog.
   **Test:** add a validation with two attached docs, see it in the list.

8. **Selection Prompt tab.** Single text area for the selection prompt.
   **Test:** edit prompt, save, see persisted value on reload.

9. **State transitions.** Buttons to "Create new Draft" (creates next version),
   "Place in Service" (Draft → In Service, supersedes prior In Service of
   same type), "View Superseded versions." All transitions enforce the agreed
   rules.
   **Test:** create v2 as Draft, place v2 in service, observe v1 moves to
   Superseded.

### Phase 4 doc commit

Standard.

---

## Phase 5 — Workflow Designer

**Goal.** Within a Request Type Draft, a "Workflows" tab opens a canvas
where compliance can lay out a workflow graph: add blocks via on-node
+ affordances, edit structure with X delete buttons, save the graph,
promote to In Service when complete.

**Risk note (as written before Phase 5).** This was the biggest phase
by far and the only one with unfamiliar UI work (JS interop for the
canvas). Chunk granularity was uncertain; the original chunk list was
a first guess. The notes below record what shipped, which differed
significantly from the original plan.

### Chunks as shipped

The original provisional chunk list (palette + drag-to-add → edge
drawing → property editor → save/load) was superseded by a design
pivot in Chunk 7. The shipped sequence:

1. **Chunks 1-3 — WorkflowDefinition + WorkflowNode + Block catalog
   repositories.** CRUD for the static graph. Nodes carry
   node_type_id, block_catalog_id (or null), path pointers, prompts,
   stale threshold. Block catalog seeded manually.

2. **Chunk 4 — Designer page shell.** Route
   `/admin/request-types/{typeId}/workflows/{workflowId}/designer`.
   Loads the type + workflow + nodes + blocks. Read-only banner when
   the parent version isn't Draft.

3. **Chunk 5 — D3 interop spike.** Pure-SVG canvas via D3 loaded
   from a vendored npm dep. JS module renders the graph; Blazor owns
   the data.

4. **Chunk 6 — Palette + drag-to-add nodes.** Left-rail palette with
   draggable items. Drop onto canvas creates a `workflow_nodes` row
   at execution_level=0 with both path FKs null (orphan-node posture).
   **This chunk was effectively reverted in Chunk 7.**

5. **Chunk 7 — Design pivot to +-button graph construction.**
   Replaced free-drag palette with a graph-construction model rooted
   at Start. Every workflow auto-creates a Start node in the workflow
   create transaction. JS draws + buttons on every non-terminal node's
   open slots; clicking + opens a block-picker dialog. New repo
   method `InsertChildAsync` atomically inserts + wires + renumbers.
   Two follow-up commits: dropped `CK_workflow_nodes_decision_both_edges`
   (move "Decision has both children" from edit-time to promotion-time)
   and a layout polish pass adding subtree-width-aware placement plus
   classic-flowchart L-shape edges from Decision vertices.

6. **Chunk 8 — Node property editor. DEFERRED.** UI to edit prompt
   text, approver group, stale threshold per node. The repo's
   UpdateAsync already supports all the property fields; what's
   missing is the side-panel UI. Deferred at user request pending
   review of whether it's needed for Phase 5 close-out — the workflow
   engine (Phase 6+) runs without it using default property values.

7. **Chunk 9 — Delete affordances.** X (delete) button in the top-left
   of every non-Start node, opening a confirmation dialog that
   branches on node type and descendant count. Two new repo methods:
   `DeleteSubtreeAsync` (recursive cascade) and `DeleteAndSpliceAsync`
   (lift single child up + renumber). Decision delete is subtree-only
   (two subtrees can't cleanly merge); Process delete offers splice
   vs subtree; terminal delete is plain confirm; Start delete is
   blocked. Followed by a bug-fix commit (the recursive CTE was
   destroying its own walk data across two ExecuteAsync calls).

8. **Chunk 10 — Promotion-time validation + end-of-phase rollup.**
   `TransitionToInServiceAsync` now refuses promotion when any
   workflow on the version has structural issues: non-terminal nodes
   missing required children (Decision needs both branches; Start
   and Process need their single path1), orphan nodes (unreachable
   from the workflow's Start), or workflows with NULL start_node_id.
   `TransitionToInServiceResult` promoted from enum to record with
   `Outcome` + `Issues` list. UI shows the first three issues in a
   sticky snackbar. Temporary node-list readout below the canvas
   removed. Inline incomplete-node badges deferred — the dashed
   dangling-edge cue from Chunk 7 is sufficient for our scale.

### Phase 5 doc commit

- CONTINUE.md updated each chunk; final state reflects Chunks 1-10
  done (Chunk 8 deferred).
- CONCEPT.md §"Workflow designer" rewritten in the Chunk 7 commit to
  reflect the +-button construction model and promotion-time
  validation posture.
- LessonsLearned.md grew substantially through Phase 5: ten new
  entries covering the design pivot rationale, the recursive-CTE
  patterns and bugs, the MudBlazor popover-vs-dialog decision, the
  schema-CHECK-vs-promotion-gate trade-off, and the post-mortem on
  the orphan bug.
- PLAN.md (this file) — Phase 5 section updated to reflect what
  actually shipped.

### Outstanding from Phase 5 (carried forward, NOT blocking)

- **Chunk 8 deferred.** Side-panel node property editor. Pickup
  whenever it's actually needed.
- **CI not wired.** Tests run on the developer's machine via
  `dotnet test`. A buggy Chunk 9 commit shipped because the test
  that should have caught the bug existed but apparently wasn't
  executed before commit. Wiring even a minimal GitHub Action would
  prevent this class of failure. Worth doing before Phase 6.
- **Inline incomplete-node badges.** Skipped in Chunk 10 because the
  dashed-dangling-edge cues from Chunk 7 are visible enough at our
  scale. If a busier-canvas workflow shows up, add badges then.

---

## Phase 6 — AI Service + Storage + Submission Portal

**Goal.** A submitter logs in, picks a request type, uploads required docs,
types notes, submits. The page transitions to a validation results view that
updates live via SignalR as each Claude call resolves. Outcome: all pass →
request number + email; any fail → submitter sees fix-list and can re-submit.

### Chunks

1. **Storage abstraction.** `IDocumentStorage` interface in Services;
   `LocalDiskDocumentStorage` impl in Infrastructure that reads
   `Storage.BasePath` from settings and stores under `{RequestID}/{filename}`.
   Operations: store, retrieve, delete-all-for-request.
   **Test:** unit test the impl against a temp directory; integration test
   against the NAS path if available.

2. **Anthropic SDK wiring + model_pricing seed.** Add `Anthropic.SDK` NuGet
   reference. Seed at least one row in `model_pricing` for the model the AI
   service will call. Configuration: `Anthropic:ApiKey` in user secrets.
   **Test:** a tiny test page (`/test/ai`) with a button that calls Claude
   with a trivial prompt and shows the response. (Test button removed in a
   later chunk.)

3. **AI Service.** `IAiService` interface in Services. Impl in Infrastructure:
   reads `AI.Disabled` from settings (throws `AiDisabledException` if true);
   calls Claude via the SDK; computes cost from `model_pricing`; writes the
   `ai_usage` row; returns the raw response to the caller. Doesn't interpret.
   **Test:** the `/test/ai` page from chunk 2 now calls through the service;
   `ai_usage` row appears in DB after each call with correct token counts
   and cost.

4. **Validation runner.** Background task per submission: iterates over the
   Request Type Version's validations in execution_order, calls the AI
   service for each, writes a ValidationResult artifact (request-scoped),
   publishes a SignalR event per result. Sequential, not parallel.
   **Test:** invoke from a test endpoint with a fake request ID; observe
   `ai_usage` rows and `request_workflow_artifacts` rows appearing; observe
   SignalR events in browser console on the test page.

5. **Submission portal — pick request type page.** `/submit`. Logged-in user
   sees a single dropdown of in-service Request Types. Picks one → routes to
   the upload page with the type's required docs.
   **Test:** seed two Request Types in service. Pick one. See expected upload
   slots on the next page.

6. **Submission portal — upload page.** Grid of required-doc slots, file
   picker per row, notes textarea, Submit button. Submit creates the
   `requests` row (status `P`), writes files to storage, kicks off the
   validation runner, navigates to the validation results page.
   **Test:** complete the flow; row appears in `requests` with status `P`;
   files appear at `{BasePath}\{RequestID}\`.

7. **Submission portal — validation results page.** Connects to SignalR hub
   keyed on request ID. Renders validation list; rows update from spinner →
   ✓ / ✗ as events arrive. ✗ rows have a "?" affordance that opens the
   explanation. On all-pass: shows request number and triggers confirmation
   email row in `outbound_emails`. On any fail: shows re-submit affordance.
   **Test:** submit, watch live updates, see ✓ / ✗ rendering.

8. **Re-submit handling.** From the failure state, user can re-attach files
   for any (or all) slots and re-submit. Re-submit: wipes the storage
   directory, hard-deletes old `request_documents` rows, writes new files,
   re-runs all validations.
   **Test:** submit with a deliberately wrong file, see fail, re-submit with
   correct file, see pass.

### Phase 6 doc commit

Standard, plus CONCEPT.md §3.1 + §3.2 rewritten to reflect the actual portal
and validation model.

---

## Phase 7 — Workflow Engine + Reviewer Surface

**Goal.** The workflow engine walks accepted requests through their workflow.
The reviewer surface lets FT users see queues, open workflow details, make
decisions, restart, reassign.

This phase is large and may split into Phase 7a/7b when we get there. Chunk
list deferred to that point.

---

## Phase 8 — Windows Service for Background Workers

**Goal.** The workflow engine and budget polling worker move out of the web
process into `VendorSure.BackgroundWorkers`, hosted as a Windows Service.

Chunk list deferred.

---

## Phase 9 — Concept doc final pass + polish

**Goal.** `CONCEPT.md` updated to fully reflect what was built. `CONTINUE.md`
retired or converted to a "current state" doc. README polished. Any
LessonsLearned cleanup. Final review of `data-model.sql` vs reality.

Chunk list deferred.

---

## Notes on what's NOT in this plan

- **Email triggers.** Hardcoded in the code where they fire (submission
  confirmation, decision-block completion, terminal-block completion). No
  configurable email-template table.
- **Indexes on the schema.** Added during a separate pass once we know the
  query patterns.
- **MRI handoff.** A block on the last accept path of each Request Type's
  approval workflow. IT-authored, outside this plan.
- **Production deployment.** Out of scope.
- **CI.** No GitHub Actions or similar. Build/test happens on your local
  machine after sync.
