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
- The workflow engine is a dumb synchronous graph walker — it doesn't know what nodes do.
- Blocks (process + decision) are IT-authored code, composed visually by Compliance. Decisions are strictly binary. Three implied execution modes: System / AI / User.
- Artifacts are typed, produced by process blocks, consumed downstream. Documents are at the request level and survive restarts.
- Designer is a dumb canvas — no validation, no graph walking.
- Reviewer surface uses role-based pool queues. Submitters have no in-app visibility; they get emails.
- Restart is the only mid-flight intervention: admin-only, goes to start node, wipes artifacts, retains docs, preserves original instance for audit.

## What is explicitly deferred

See section 7 of `docs/CONCEPT.md` for the full open-items list. Headline items:

- Block catalog mechanics and block-code versioning.
- Detailed artifact lifecycle (multiple of the same type, etc.).
- Three execution modes expressed in interfaces.
- "Stalled" definition.
- Submitter email triggers.
- Full admin function list.
- Request Type lifecycle states and transitions.
- Restart audit linkage in the reviewer view.

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
