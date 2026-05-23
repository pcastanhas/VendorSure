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
- **Request Types screen:** list of active request types. New / edit (double-click).
- **Request Type editor:** a header section (name, created date, version, audit info) and a tabbed body. Known tabs so far:
  - **Workflows** — the workflows that can service this request type. Clicking a workflow opens the workflow designer.
  - **Required Documents** — the documents the submission portal must demand for this request type.
  - **Validation Prompts** — the prevalidation prompt for the AI triage layer.
  - **Workflow Selection Prompts** — the workflow-selection prompt for the AI triage layer.
- **Workflow designer:** a visual flowchart canvas with start nodes, process nodes (rectangles), decision nodes (diamonds), and terminal nodes. Compliance composes workflows by dragging IT-authored blocks onto the canvas and wiring them together.
  - The designer is a **dumb canvas**. It does **not** validate the graph, walk branches, type-check artifact inputs/outputs, or warn about unreachable nodes. If a design is broken (e.g., a block needs an artifact that no upstream block produces), it fails at runtime and gets fixed in a new Request Type version.

#### Versioning & immutability

This is structural and worth stating plainly:

- A Request Type is the container. Required docs, prompts, candidate workflows, and the workflows themselves all hang off it as children.
- Once a Request Type version is **placed in service**, it is **immutable**. Nothing about it — not the docs list, not the prompts, not the workflows, not any node within a workflow — can change.
- To change anything, you create a **new version** of the Request Type and place that in service.
- **Snapshot semantics:** a request that started under v1 finishes under v1, even after v2 is placed in service. Each running request is bound to its Request Type version for life.
- Multiple in-service or recently-in-service versions of the same Request Type can coexist at any moment. The latest is what new requests use; older versions are kept alive as long as any live request still references them.

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

#### Cancellation

- Cancellation is **a user-decision outcome**, not a separate mechanism. Wherever a workflow is parked for a human decision, "Cancel" can be one of the available outcomes.
- Submitters cannot cancel. Admins cannot cancel arbitrarily. The decision-making user at a paused node can.
- Choosing Cancel moves the workflow immediately to the Cancelled terminal node.

#### Restart

- The only mid-flight intervention.
- Admin-initiated.
- Goes back to the **start node** of either the same workflow or a different workflow (this is how routing mistakes by the AI are corrected).
- All artifacts are deleted. All alarms reset. Documents and submitter notes are retained.
- The original workflow instance is preserved on the request for audit; the restart creates a new workflow instance on the same request.

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
  - Three execution modes implied across both block kinds: **System** (deterministic code), **AI** (a Claude call), **User** (a human decision in the reviewer surface).
- **Artifact** — a typed object produced by a process block, consumed by downstream blocks. Lives in the workflow instance's artifact bag. Blocks request artifacts of the types they need; the workflow returns them; blocks process them as they see fit.
- **Document** — uploaded by the submitter. Lives at the **request** level, not the workflow-instance level. Survives restarts.

## 5. Audit & Immutability Posture

- **Capture generously.** Every Claude call's inputs, outputs, prompt version, and model version is persisted. Every block produces a log pass-or-fail. Every user decision records who, when, what they saw, and what they chose. Every override of an AI decision is captured.
- **Freeze the past.** Request Type versions are immutable once placed in service. In-flight requests run on their original version forever. Old versions are preserved as long as any request still references them.
- **Preserve history through change.** Restarting a request preserves the original workflow instance on the request; the restart adds a new workflow instance alongside it.
- **Mineable corpus.** Persisted AI calls + decisions + overrides form a training corpus for future prompt refinement and analysis.

## 6. Cross-cutting Implementation Decisions Deferred

Settled at concept level; details deferred to the design phase.

- Single deployable app vs. multiple services.
- Database / persistence technology.
- How MRI hand-off happens after a request is approved.
- Email infrastructure (transport, templating).
- Identity integration specifics (Entra / SSO config).

## 7. Open Items

Explicitly deferred during the concept session, to be resolved in design:

- **Block catalog mechanics.** How IT-authored blocks register into the catalog and surface in the designer's palette.
- **Block code versioning.** Whether in-flight workflows pin to a specific block-code version or pick up the latest. (This is the most consequential open versioning question.)
- **Artifact lifecycle in detail.** Multiple artifacts of the same type in one workflow (e.g., two OFAC payloads for two principals) — selection semantics for consuming blocks.
- **Document vs. artifact boundary.** Confirmed at concept level (documents at the request, artifacts at the workflow instance, restart wipes artifacts but keeps documents). Storage and identity details deferred.
- **Three block execution modes.** System / AI / User — to be expressed in the block base interface and the engine's wake protocol.
- **"Stalled" definition.** Likely the union of alarm-fired, last-process-failed, and untouched-for-N-days. Exact rule TBD.
- **Decision notes.** Whether reviewer notes on a user decision are always optional or sometimes required (e.g., on Reject).
- **Submitter email triggers.** Exact list of events that emit an email to the submitter.
- **Admin panel — other admin functions.** Beyond Request Types, the admin panel will likely need Users & Roles, Prompt management (IT-side), and Audit / Activity viewers. Full list TBD.
- **Request Type lifecycle states.** Implied: Draft (editable) → In Service (immutable, accepting new requests) → Deprecated (no new requests, in-flight only) → Retired (no live requests). Transition rules TBD.
- **Restart audit linkage.** How prior workflow instances are linked to the request after a restart, and how they surface in the reviewer view.
