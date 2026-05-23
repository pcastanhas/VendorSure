# REMOVE-BEFORE-PROD.md

> Everything in this list must be removed before production deployment.
> Production deployment is out of scope for the current effort, but this
> file is maintained as we go so that the cutover (if and when it happens)
> is a mechanical checklist, not an archaeology project.
>
> Convention: any code that needs to be removed before prod is tagged with
> `REMOVE-BEFORE-PROD` in a nearby comment. Running
> `grep -r "REMOVE-BEFORE-PROD" src/` gives the exhaustive hit list.

## Items

_None yet. First entries arrive in Phase 1 / Chunk 2 (debug identity shim)._

## Cutover checklist (when the time comes)

1. Confirm Entra app registration is fully configured and reachable.
2. For each item below, delete the code and the corresponding setting (if
   any).
3. Search for any remaining `REMOVE-BEFORE-PROD` markers and remove them.
4. Build clean. Run integration tests. Verify auth still works against real
   Entra.
5. Delete this file.
