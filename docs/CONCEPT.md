# VendorSure — Concept

> Status: **Concept document**, living. This captures the high-level design agreed in the first design session. Implementation detail is deliberately out of scope; open items are listed at the end.

## 1. Purpose

VendorSure is an internal pre-MRI vendor verification system for a large real estate investment company.

Today, vendor onboarding and change management is a manual process run by a Vendor Management team and a Compliance team. Every new vendor, name change, address change, bank info change, or data correction must be reviewed and verified by humans before being entered into MRI. Each request follows one of several distinct workflows depending on factors such as US vs. Foreign, Public vs. Private, and High-Risk classification.

VendorSure replaces the manual process with a structured, auditable system that:

- Captures requests with required supporting documents.
- Uses AI (Claude) to perform non-deterministic validation and routing.
- Uses deterministic system code for things that must be deterministic (e.g., OFAC pulls).
- Walks each request through a versioned, Compliance-defined workflow.
- Surfaces work to human reviewers for the decisions that humans must own.
- Preserves a full audit trail of everything: documents, AI calls, artifacts produced, decisions made, who, when.

VendorSure is **not** the system of record for vendor master data. MRI remains authoritative. Approved vendor data flows downstream into MRI; that hand-off is out of scope for VendorSure itself.

## 2. Scale and Users

- **~5,000** existing vendors in MRI.
- **~200–300** new vendor requests per year.
- **~700–800** total requests per year including changes and corrections.
- **5 full-time power users** across Vendor Management and Compliance — the heavy users of the reviewer surface.
- **A long tail of part-time submitters** — internal staff who only interact with the submission portal when they need something done.

The split between heavy and light users drives a key UX split: dense, queue-driven, keyboard-friendly work surface for the FT users; minimal, guided, low-friction intake for everyone else.

## 3. The Five Subsystems

VendorSure is structured as five logical subsystems. Whether they are deployed as one app or many is an implementation decision deferred to the design phase; at concept level the boundaries are logical.

### 3.1 Submission Portal

The intake surface for light users.

- Internal-only, secured by Entra / SSO. Public DNS, tenant-locked.
- Two entry paths into the system overall:
  - **Light path** — a link in MRI brings the user to the submission portal.
  - **Heavy path** — FT users go directly to the full reviewer app (subsystem 3.5).
- Submission flow:
  1. User picks a request type (e.g., "New Vendor", "Bank Info Change").
  2. The portal shows the list of required documents for that request type.
  3. User uploads **every** required document. Submission is hard-gated — no submit until all required docs are attached. (Rationale: an incomplete request would be rejected anyway.)
  4. User optionally adds a clarifying note.
  5. User submits. The system returns a request number and emails a confirmation.
- Re-entry: a submitter can return to the portal, look up their request by number, and add notes or documents. Access is authorized by matching the Entra identity to the original submitter on that request number.
- The portal is **dumb on purpose**: no vendor lookup against MRI, no duplicate-vendor check, no client-side classification beyond picking the request type. All of that cleverness lives in the AI triage layer.

### 3.2 AI Triage Layer

The first thing that happens when a new (or re-submitted) request arrives.

- Backend = the Claude API.
- **Two sequential, stateless Claude calls** per evaluation:
  1. **Prevalidation call.** Inputs: request type, attached documents, requestor notes, the prevalidation prompt. Output: a structured summary of what's present, what's missing, what looks wrong.
  2. **Workflow selection call.** Runs only if prevalidation passed. Inputs: the summary from the prevalidation call, the catalog of candidate workflows that can service this request type, and a selection prompt encoding the Compliance-authored decision tree (with a designated default workflow for the uncertain case). Output: the name of the workflow to run.
- Three possible end-states from the triage layer:
  - **Pass** → workflow is selected and the request is handed to the workflow engine.
  - **Fix-and-resubmit** → an email is sent to the submitter listing what's missing or wrong. The request is parked until the submitter re-submits via the portal.
  - **Reject** → an explanatory rejection email is sent to the submitter. The request is terminal.
- Statelessness: every Claude call is a fresh call with the full payload. No conversation memory, no persisted server-side context. Re-submissions re-run from scratch.
- The **only place** Claude's output drives a non-reversible system action without a human in the loop is workflow selection. Even there, a human reviewer can change the workflow mid-flight (via the Restart mechanism), and every such override is captured as training data for prompt refinement.
- **Audit:** every Claude call — inputs, outputs, prompt version, model version, decision — is persisted on the request timeline. Capture is intentionally generous; future analysis of the corpus is a first-class goal.

#### Prompt authorship

Prompts are first-class versioned artifacts. Two roles share authorship:

- **Compliance** owns the *decision tree* — the business logic of which workflow to choose, expressed in their natural language.
- **IT** owns the *prompt* — translating Compliance's decision tree into a Claude-shaped instruction set.

The prompt is what the system uses at runtime. The decision tree is the source of truth Compliance owns.

### 3.3 Admin Panel

Where Compliance and admins configure the system. Gated by `user.is_admin = true`.

- Azure / Entra-style admin shell — left navigation with admin functions. "Request Types" is one such function; others are TBD.
- **Settings** — the first admin section built (Phase 1). System-wide
  configuration rows from the `settings` table, edited in a `MudTable` +
  `MudDialog` pattern: list view with sensitive values masked, edit
  dialog with reveal-toggle for sensitive values, required-field
  validation on save. This page is the template the other admin pages
  (Users, User Groups, Required Documents Library, Request Types) will
  follow.
- **User Groups** and **Users** — Phase 2. Both follow the Settings
  pattern (list / new+edit dialog / snackbar / refresh) but expanded
  for true CRUD. Two cross-table business rules are enforced in
  repository SQL: a group cannot be deactivated while users are
  assigned to it, and users cannot be assigned to inactive groups —
  together they maintain the invariant "no user points at an inactive
  group" from both directions. Each repository's update operation
  returns a result enum so the UI can map specific rejection reasons
  (entraid collision, inactive group, has-users) to distinct snackbar
  messages.
- **Required Documents** — Phase 3. The catalog of document types
  Request Types can later demand from submitters. Same list / dialog
  shape as the Phase 2 admin pages, plus a first-of-its-kind hard
  delete: the repository allows deletion only when no Request Type
  version references the row, enforced atomically in the DELETE
  statement's WHERE clause. The UI confirms before deleting via
  `MudDialogService.ShowMessageBoxAsync`.
- **Request Types** — Phase 4. The full editor surface: a list page
  at `/admin/request-types` shows each type with chip-styled
  indicators for its current In Service / Draft / Superseded
  versions; a detail page at `/admin/request-types/{id}` opens the
  editor proper. The list "New request type" affordance creates the
  type and its initial Draft v1 atomically in one transaction
  (`CreateWithFirstDraftAsync`), so a freshly-created type is
  immediately ready to edit.
- **Request Type editor:** three vertically-stacked sections plus
  four tabs.
  - **Top: type-level edit.** Name, IsActive, IsExplanationRequired,
    Save. These fields live on `request_types` (one row per type, not
    per version) so editing them applies across all versions — the
    immutability rule is per-version, not per-type.
  - **Middle: version selector + audit.** Dropdown of all versions
    (defaulting to Draft if one exists, else In Service, else most
    recent Superseded), three labelled timestamp rows (Created,
    Placed in service, Superseded), and the two transition buttons
    ("Place v{N} in service" when the displayed version is Draft;
    "Create new Draft" when no Draft exists). When the displayed
    version is not Draft, an info banner explains it's immutable and
    points the admin toward the existing Draft or the Create-Draft
    button.
  - **Bottom: four tabs.**
    - **Workflows** — list of workflows on the current version with
      a "New workflow" button. Opens the workflow designer page on
      a separate route. Editable on Draft; read-only otherwise.
      Backed by the Phase 5 designer.
    - **Required Documents** — table of the document-type library
      with an Attached checkbox and Required switch per row. Toggling
      Attached calls the junction repo's Add/Remove; Required calls
      SetRequired. When the displayed version isn't Draft, only the
      attached rows are shown and all controls are disabled (a
      read-only audit view of what was attached when the version
      was placed in service).
    - **Validations** — table of validations in execution order with
      Add / Edit / Delete and a Documents sub-picker per row. The
      sub-picker is scoped to the version's currently-attached
      required documents because the validation-document junction
      enforces same-version atomically in the INSERT — the schema
      FKs only enforce existence, not version match. Creating a
      validation auto-appends its execution_order
      (`ISNULL(MAX, 0) + 1`); reordering isn't exposed in v1.
    - **Selection Prompt** — single multi-line textarea bound to
      the version's `workflow_selection_prompt`. Save enabled only
      when dirty relative to what was loaded; empty/whitespace
      normalises to NULL in the column. Read-only when the displayed
      version isn't Draft.
  - **State transitions.** Placing a Draft in service is the first
    multi-row state mutation in the system: in one transaction the
    prior In Service version of the same type (if any) is demoted to
    Superseded with `superseded_ts`, and the target Draft is promoted
    to In Service with `placed_in_service_ts`. Both rows take the
    same `DateTime` value so the audit timestamps line up exactly.
    An UPDLOCK on the initial Draft check serialises concurrent
    callers attempting to promote the same Draft, so only one
    transition can succeed.
- **Workflow designer:** a visual flowchart canvas with start nodes, process nodes (rectangles), decision nodes (diamonds), and terminal nodes. Compliance composes workflows by clicking + buttons on existing nodes and picking the next block from a dialog. **Phase 5.** The Phase 4 Workflows tab is the entry point.
  - **Construction rooted at Start.** Every workflow has a Start node, auto-created the moment the workflow is created (same transaction). The user can't have a workflow without one. The graph grows from Start: each non-terminal node has a + affordance on every open slot (one + for Start/Process, two + for Decision). Clicking + opens a block picker; the chosen block becomes the slot's child. If the slot already has a child, the new block is inserted *between* the parent and the existing child (the existing child becomes the new node's child).
  - **Block catalog drives node identity.** Each Process and Decision node references a `block_catalog` row. The catalog carries the block's `name` (the short label shown on the canvas and as the primary line in the picker), `description` (longer prose shown as a secondary picker line and as a native hover tooltip on the node), and `class_name` (the .NET implementation invoked at runtime). Block code is precoded by IT — the workflow author picks blocks from the catalog, never defines their internals.
  - **Decision blocks carry path labels.** A Decision block's code precodes both the predicate (what's evaluated) and the meanings of path1 and path2 ("True"/"False", "Approved"/"Denied", "Clean"/"Flagged"). Those labels live on `block_catalog.path1_decision` and `path2_decision` — they're block-level semantics, not workflow-author choices. The canvas renders them on the horizontal edge segments leaving the diamond's left and right vertices, in a neutral muted color (no red/green coding — see lesson 23) so the reader sees what each branch means without confusing the labels with positive/negative judgments. The actor that evaluates the predicate is also precoded: a human Decision block routes through an approver UI; a system Decision block evaluates a predicate; an AI Decision block prompts an AI service. Same canvas affordance for all three.
  - **Deletion via X buttons.** Every non-Start node has an X (delete) button in its top-left corner. Clicking opens a confirmation dialog whose copy and action buttons depend on the node type: terminals and childless Process nodes get a plain confirm; Process nodes with descendants offer splice-into-parent (the surviving child is lifted up one level, renumbered) vs delete-subtree; Decision nodes offer subtree-delete only (their two branches can't cleanly merge). Start is never deletable.
  - **Structural validity is enforced by the UI affordances.** The user can't drop a Start at the wrong level, can't orphan a node, can't add children to terminals — those interactions don't exist. Schema CHECKs remain the safety net for any code path that reaches the database directly.
  - **Promotion-time validation.** A workflow can be left half-wired in Draft (some Decisions missing a branch, some Start/Process pointing at nothing). The version's `Draft → InService` transition in `TransitionToInServiceAsync` validates every workflow on the version and refuses promotion if any structural problem is found: a non-terminal node missing required children (Decision needs both branches; Start and Process need their single path1), an orphan node (unreachable from the workflow's Start), or a workflow with no Start. The rejection result lists each concrete problem so the user knows what to fix; the UI surfaces the first few in a snackbar that requires interaction to dismiss.
  - **Designer-time semantic validity is still out of scope.** The designer does **not** type-check artifact inputs/outputs between blocks or warn about unreachable runtime states. If a design is structurally well-formed but semantically broken (e.g., a block needs an artifact that no upstream block produces), it fails at runtime and gets fixed in a new Request Type version.

#### Versioning & immutability

This is structural and worth stating plainly:

- A Request Type is the container. Required docs, prompts, candidate workflows, and the workflows themselves all hang off it as children.
- Once a Request Type version is **placed in service**, it is **immutable**. Nothing about it — not the docs list, not the prompts, not the workflows, not any node within a workflow — can change.
- To change anything, you create a **new version** of the Request Type and place that in service.
- **Snapshot semantics:** a request that started under v1 finishes under v1, even after v2 is placed in service. Each running request is bound to its Request Type version for life.
- Multiple in-service or recently-in-service versions of the same Request Type can coexist at any moment. The latest is what new requests use; older versions are kept alive as long as any live request still references them.

#### Lifecycle states

A Request Type version moves through three states:

- **Draft** — editable. No live requests are bound to it. Anything about it can change.
- **In Service** — immutable. New requests bind to this version. Exactly one version of a given Request Type is In Service at a time.
- **Superseded** — a newer version of the same Request Type is now In Service. No new requests bind to a Superseded version, but in-flight requests already bound to it finish on it. A Superseded version is retained as long as any request still references it.

### 3.4 Workflow Engine

The orchestrator. Walks workflow instances from start to terminal.

- **Synchronous graph walker.** Picks up a request, walks node-by-node, parks on async waits, resumes on wake. That is the entire job.
- **Dumb on purpose.** The engine does **not** know what any node does. It knows only:
  - How to start a node.
  - How to detect when a node is finished.
  - How to read the node's output to choose the next edge (for decision nodes) or to continue (for process nodes).
  - How to persist workflow state in between.
- The **workflow instance** is the universe for a running request — it carries the request, the documents, the artifact collection, the audit log, the user decisions, the alarms, and the current position in the graph.

#### Failure model

- Each block handles its own retry logic. The engine does **not** retry.
- Every block produces a full execution log regardless of pass or fail.
- On failure, a block produces no artifact, but is still "done" from the engine's perspective — just with no artifact and an error log.
- Downstream consequences are intrinsic, not engine-managed:
  - A downstream process that requires the missing artifact will itself fail.
  - A downstream decision block that depends on the missing artifact will route to Reject (because something is missing).
- The engine has no "this whole workflow is stuck" concept. Failures cascade through the graph naturally.

#### Alarms

- First-class, configured per node, time-based.
- Side-effect only — they produce emails (e.g., reminders to the assigned reviewer). They do not change workflow state.
- Especially useful on user-decision nodes where waits are unbounded.

#### Stalled

The engine has no internal "stuck" state (see *Failure model* above). "Stalled" is a presentation concept used by the reviewer surface to populate the Stalled Workflows queue, defined as the union of:

- An alarm has fired on the current node, **or**
- The last process block on this instance failed, **or**
- The instance has been untouched for more than N days (N TBD in design).

Being Stalled changes nothing about the workflow's execution; it only changes which queue surfaces it to reviewers.

#### Cancellation

- Cancellation is **a user-decision outcome**, not a separate mechanism. Wherever a workflow is parked for a human decision, "Cancel" can be one of the available outcomes.
- Submitters cannot cancel. Admins cannot cancel arbitrarily. The decision-making user at a paused node can.
- Choosing Cancel moves the workflow immediately to the Cancelled terminal node.

#### Restart

- A mid-flight intervention on a workflow instance.
- **In-place on the same workflow instance.** Restart does **not** create a new instance, and it does **not** change which workflow the instance is bound to. The shape of the workflow is preserved; the pointer just moves.
- The current node pointer is reset to the start node of the workflow the instance is already running.
- All artifacts are deleted. All alarms reset. Documents and submitter notes are retained.
- The instance's audit log retains everything that happened pre-restart. The restart itself is recorded as another event on the timeline.
- Mechanics (who can trigger it, when it's allowed) deferred to the design phase.

#### Workflow reassignment

A separate mechanism from Restart. Used when the wrong workflow is running for the request (e.g., to correct an AI routing mistake from the triage layer).

- The current workflow instance is moved to its **Cancelled** terminal node.
- A new workflow is attached to the request and a new workflow instance is started on it from the start node.
- Over its lifetime, a request can carry multiple workflow instances — at most one live at a time, plus any number of terminal ones (Approved / Rejected / Cancelled). Each instance is bound to a single workflow for its entire life.
- Mechanics (who can trigger it, audit linkage between the cancelled and the new instance) deferred to the design phase.

### 3.5 Reviewer Surface

The work-queue web app for the FT users.

- Left navigation lists queues:
  - **All Active Workflows**
  - **My Reviews**
  - **Stalled Workflows**
  - (Others may be added.)
- Click a queue → list of workflows in it. Click a workflow → full workflow detail view.
- The detail view shows **everything**:
  - The workflow graph, rendered as a simplified dots-and-edges view (the rich block rendering belongs to the designer in the admin panel; reviewers see an abridged form).
  - All documents.
  - All artifacts produced so far.
  - All logs.
  - All prior decisions.
  - The pending decision (if any) — front and center, with whatever supporting information the decision needs.
- **Role-based pool routing.** User-decision blocks are assigned to a *role* (e.g., "Compliance Reviewer"). Any user in that role sees the work in their queue. First user to act wins. Avoids per-individual stalls.

#### Submitter visibility

Submitters have **no** in-app visibility into in-flight workflow state. They get:

- A confirmation email at submission with their request number.
- A progress email at each decision-block execution.
- A final approval / rejection email.

If they need to add information, the AI triage layer or a reviewer can trigger an email asking for it, and the submitter returns to the submission portal to add docs or notes.

## 4. Core Domain Primitives

The shapes that recur across the subsystems. These are concept-level, not the data model.

- **Request** — one submission, one implicit vendor target, one change. Carries the documents and the submitter note. Has a lifecycle: submitted → in triage → in workflow → terminal.
- **Request Type** — the configuration container. Versioned, immutable once placed in service. Owns required docs, validation prompt, selection prompt, candidate workflows.
- **Workflow** — a directed graph belonging to a Request Type version. Start → process/decision nodes → terminal nodes (Approved / Rejected / Cancelled).
- **Workflow Instance** — a live run of a workflow for a specific request. Holds documents, artifacts, audit log, current position, alarms.
- **Block** — IT-authored code, registered into a catalog, composed visually in the designer. Two kinds:
  - **Process block** (rectangle): declares required input artifact types, produces a typed output artifact on success.
  - **Decision block** (diamond): declares required input artifact types, returns one of exactly two outgoing paths (`Path1` or `Path2`), each with a label. **Decisions are strictly binary.** N-way classification is achieved by an upstream AI process emitting typed classification artifacts (e.g., `ForeignDesignation`, `RiskProfile`, `PublicOrPrivateCompany`) and then routing via a chain of binary decisions.
  - How a block does its work — deterministic system code, a Claude call, parking for a human decision in the reviewer surface — is internal to the block's IT-authored implementation. The engine and the designer see only the block interface.
- **Artifact** — a typed object produced by a process block, consumed by downstream blocks. A block that declares a required input artifact type receives **all** available artifacts of that type from the workflow instance's artifact bag; the block's own logic decides how to use them.
- **Document** — uploaded by the submitter. Lives at the **request** level, not the workflow-instance level. Survives restarts.

## 5. Audit & Immutability Posture

- **Capture generously.** Every Claude call's inputs, outputs, prompt version, and model version is persisted. Every block produces a log pass-or-fail. Every user decision records who, when, what they saw, and what they chose. Every override of an AI decision is captured.
- **Freeze the past.** Request Type versions are immutable once placed in service. In-flight requests run on their original version forever. Old versions are preserved as long as any request still references them.
- **Preserve history through change.** A restart preserves the pre-restart history on the same workflow instance; the restart is just another event on the instance's audit log. When a request is moved to a different workflow via workflow reassignment, the cancelled instance and the new instance both remain on the request.
- **Mineable corpus.** Persisted AI calls + decisions + overrides form a training corpus for future prompt refinement and analysis.

## 6. Cross-cutting Implementation Decisions Deferred

Settled at concept level; details deferred to the design phase.

- Single deployable app vs. multiple services.
- Database / persistence technology.
- How MRI hand-off happens after a request is approved.
- Email infrastructure (transport, templating).
- Identity integration specifics (Entra / SSO config).

## 7. Open Items

What remains genuinely open at the concept level. Everything else is either resolved above or is an implementation detail deferred to the design phase (in which case it lives in §6 or in a per-subsystem deferral below).

- **Document and artifact storage and identity.** The boundary is settled (documents at the request, artifacts at the workflow instance, restart wipes artifacts but keeps documents). Storage technology and identity details deferred to design.
- **Stalled threshold.** The rule is settled (alarm-fired ∨ last-process-failed ∨ untouched-for-N-days). The value of N is TBD.
- **Restart mechanics.** Who can trigger it, when it's allowed.
- **Workflow reassignment mechanics.** Who can trigger it, when it's allowed, how the cancelled and the new instance link in the audit view.
- **Decision notes.** Whether reviewer notes on a user decision are always optional or sometimes required (e.g., on Reject).
- **Submitter email triggers.** Exact list of events that emit an email to the submitter.
- **Admin panel — other admin functions.** Beyond Request Types, the admin panel will likely need Users & Roles, Prompt management (IT-side), and Audit / Activity viewers. Full list TBD.
- **Request Type lifecycle transitions.** The three states are settled (Draft → In Service → Superseded). The transition rules — what triggers each move, what's allowed in each state — TBD.

The following were explicitly closed during the concept phase and are recorded here so we don't relitigate them:

- **Block catalog and block-code versioning** — out of scope for VendorSure. Blocks are authored outside the app; the catalog uses whatever block code is registered at runtime. IT development policy tracks block versions externally.
- **Multiple artifacts of the same type** — not the engine's concern. A block that requires an input artifact type receives all available artifacts of that type and decides internally how to use them.
- **Block execution modes (System / AI / User)** — not a first-class distinction. There is one block interface; how a block does its work is internal to its IT-authored implementation.
- **Restart audit linkage** — non-issue. Restart is in-place on the same workflow instance; the pre-restart history lives on the same instance's audit log.
