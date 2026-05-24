# VendorSure

VendorSure is an internal pre-MRI vendor verification system for a large real estate investment company. It tracks the lifecycle of vendor onboarding and change requests — from submission through verification to approval — before vendor data flows downstream into MRI.

## Status

Phases 1-5 shipped. Foundation, identity admin, document catalog admin,
Request Type editor, and Workflow Designer (including admin Blocks page)
are functional. Phase 6 (AI Service + Storage + Submission Portal) is next.

## Documents

- **[docs/CONCEPT.md](docs/CONCEPT.md)** — High-level concept and design of the system. Some sections (§3.1, §3.2) are stale and will be refreshed during Phase 6 and Phase 9.
- **[docs/data-model.sql](docs/data-model.sql)** — The reviewed schema definition. Source of truth for what's in the DB.
- **[docs/PLAN.md](docs/PLAN.md)** — Build plan: phases and chunks, in order. Living doc.
- **[BUILD.md](BUILD.md)** — How to build, run, test locally. Grows with the app.
- **[LessonsLearned.md](LessonsLearned.md)** — Things we figured out the hard way.
- **[docs/REMOVE-BEFORE-PROD.md](docs/REMOVE-BEFORE-PROD.md)** — Items that must be removed before any production deployment.
- **[CONTINUE.md](CONTINUE.md)** — Session handoff. Read first when picking the project back up.

## Approach

Three phases in order:

1. **Document** — capture the concept. ✓ done.
2. **Design** — detailed technical design. ✓ substantially done (data model and decisions in `docs/`).
3. **Build** — implement per `docs/PLAN.md`. ← we are here.

Build runs chunk by chunk. One commit per chunk. After each phase, a docs commit.
