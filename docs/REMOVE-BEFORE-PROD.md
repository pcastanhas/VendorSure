# REMOVE-BEFORE-PROD.md

> Everything in this list must be removed before production deployment.
> Production deployment is out of scope for the current effort, but this
> file is maintained as we go so that the cutover (if and when it happens)
> is a mechanical checklist, not an archaeology project.
>
> Convention: any code that needs to be removed before prod is tagged with
> `REMOVE-BEFORE-PROD` in a nearby comment. Running
> `grep -rn "REMOVE-BEFORE-PROD" src/` gives the exhaustive hit list.

## Items

### Debug identity shim (Phase 1 / Chunk 4)

Authenticates every request as a single hard-configured user from
`appsettings.json` instead of going through Entra. Lets us develop and
test against a logged-in user before the Azure app registration is ready.

**Code locations:**

- `src/VendorSure.UI/Authentication/Debug/` — entire folder. Delete it.
  - `DebugIdentityOptions.cs`
  - `DebugAuthenticationHandler.cs` (also contains `DebugAuthenticationDefaults`
    and `DebugAuthenticationSchemeOptions`)
  - `DebugIdentityExtensions.cs`
- `src/VendorSure.UI/Program.cs` — remove:
  - The `using VendorSure.UI.Authentication.Debug;` line (tagged
    `REMOVE-BEFORE-PROD`).
  - The `builder.Services.AddDebugIdentity(...)` call (in its own
    `REMOVE-BEFORE-PROD` block).
  - Replace these with the real Entra auth wiring.
- `src/VendorSure.UI/appsettings.example.json` — remove the `Debug:Identity`
  section. The example then no longer documents the shim.

**Config:**

- `appsettings.json` (per-developer file, not committed) — the deployed
  configuration must not contain a `Debug:Identity` section. The shim's DI
  registration hard-refuses to load when `ASPNETCORE_ENVIRONMENT=Production`,
  so a misconfigured deployment still fails closed, but this is
  belt-and-suspenders.

## Cutover checklist (when the time comes)

1. Confirm Entra app registration is fully configured and reachable.
2. Implement real Entra authentication in `Program.cs` (typically
   `AddAuthentication().AddMicrosoftIdentityWebApp(...)`).
3. Delete the `src/VendorSure.UI/Authentication/Debug/` folder.
4. Remove the two `REMOVE-BEFORE-PROD` blocks in `Program.cs` (the `using`
   import and the `AddDebugIdentity` call).
5. Remove the `Debug:Identity` section from `appsettings.example.json`.
6. Search for any remaining `REMOVE-BEFORE-PROD` markers:
   `grep -rn "REMOVE-BEFORE-PROD" src/ docs/`. The only hits should be in
   this file and (transiently) any commit messages.
7. Build clean. Run integration tests. Verify auth still works against real
   Entra.
8. Delete this file.
