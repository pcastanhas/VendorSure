# CONTINUE.md

> Session handoff. Read this first when picking the project back up.

## Where we are

**Phase 1 — Document.** The high-level concept has been agreed and captured in `docs/CONCEPT.md`. No code yet. No design doc yet.

We are working in three phases, in order:

1. **Document** ← we are here (and substantially done with the first pass)
2. **Design** — detailed technical design on top of the concept.
3. **Build** — implementation.

## What is settled (see `docs/CONCEPT.md` for full detail)

- The five subsystems: Submission Portal, AI Triage Layer (Claude), Admin Panel, Workflow Engine, Reviewer Surface.
- Two stateless Claude calls in triage: prevalidation, then workflow selection.
- Request Types are versioned and immutable once placed in service. In-flight requests run on their original version forever.
- Request Type lifecycle states: **Draft → In Service → Superseded**.
- The workflow engine is a dumb synchronous graph walker — it doesn't know what nodes do.
- Blocks (process + decision) are IT-authored code, composed visually by Compliance. Decisions are strictly binary. **No first-class System / AI / User distinction** — one block interface; the implementation decides how it works.
- A block that declares an input artifact type receives **all** available artifacts of that type; the block decides internally how to use them.
- Artifacts are typed, produced by process blocks, consumed downstream. Documents are at the request level and survive restarts.
- Designer is a dumb canvas — no validation, no graph walking.
- Reviewer surface uses role-based pool queues. Submitters have no in-app visibility; they get emails.
- "Stalled" is a presentation concept for the reviewer surface: alarm-fired ∨ last-process-failed ∨ untouched-for-N-days. N is TBD.
- **Restart is in-place** on the same workflow instance. Resets pointer to the workflow's start node, wipes artifacts, resets alarms, retains documents and submitter notes. Does **not** create a new instance and does **not** change which workflow the instance runs.
- **Workflow reassignment** is a separate mechanism. The current instance is cancelled (moves to Cancelled terminal); a new workflow is attached to the request and a new instance is started on it. This is how AI routing mistakes get corrected.
- Block catalog mechanics and block-code versioning are **out of scope** — blocks are authored outside the app; IT dev policy tracks versions.

## What is explicitly deferred

See §7 of `docs/CONCEPT.md` for the full list. Headline items remaining:

- Document and artifact storage / identity details.
- Stalled threshold N (days).
- Restart and workflow-reassignment mechanics (who, when).
- Decision-note requirements.
- Submitter email triggers.
- Other admin-panel functions beyond Request Types.
- Request Type lifecycle transition rules.

## Approach rules (from the user)

- Do **not** write code or documents before agreement. The user will say when.
- Commit and push **directly to `main`**. No PRs.
- At the start of each session the user will provide a short-lived fine-grained PAT scoped to the repo. Clone if not already local, read files at the root.

## Sandbox / tooling notes

### .NET 10 SDK install (Ubuntu Noble)

The SDK is published in Ubuntu Noble's main archive — no Microsoft repo needed. Both `archive.ubuntu.com` and `security.ubuntu.com` are in the egress allowlist.

```
apt-get update && DEBIAN_FRONTEND=noninteractive apt-get install -y dotnet-sdk-10.0
```

`sudo` may or may not be needed depending on whether the session starts as root. In this sandbox it does not — running as root.

Installs to `/usr/lib/dotnet/sdk` with the CLI symlinked at `/usr/bin/dotnet`.

The first `apt-get install` after a fresh sandbox can fail with 404s on slightly-stale URLs because of patch bumps between the cached index and the live mirror — `apt-get update` first, then retry.

### Important limitation: NuGet is blocked

`api.nuget.org` is **not** in the egress allowlist. Consequence: `dotnet restore` and everything downstream (`build`, `publish`, `test`, `run`) cannot fetch packages from the sandbox.

The SDK is still useful in-sandbox for:

- Static inspection of source files.
- Template generation (`dotnet new ...`).
- CLI-shape checks (project file structure, etc.).

**Build verification has to happen on CI**, not in the sandbox.

## Repo state at end of this session

- `README.md` — repo intro.
- `docs/CONCEPT.md` — the agreed concept.
- `CONTINUE.md` — this file.
- No code, no solution, no project files.

## Suggested next session

The user will choose, but the natural next move is one of:

- **Design phase kickoff** — start producing a technical design doc (data model, component boundaries, API shapes, persistence choices, deployment shape) on top of the concept. Likely lands at `docs/DESIGN.md` and probably grows into multiple docs (e.g., per-subsystem).
- **Fill in open items first** — pick from the deferred list in `docs/CONCEPT.md` §7 and resolve them at concept level before moving to design.

Either way: do not write code or design docs without the user's go-ahead.
