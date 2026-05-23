# BUILD.md

> How to build, run, and test VendorSure locally.

## Prerequisites

- **.NET 10 SDK** (10.0.107 or later)
- A **SQL Server** reachable from your dev machine. Local Express, remote
  dev box, or container — all fine. The DB name should be `VenSure` to
  match what `data-model.sql` expects.
- The `VenSure` database created and `docs/data-model.sql` applied against
  it. From Chunk 3 onwards the app talks to the DB at startup (reachability
  check); from Chunk 4 onwards it also queries the `users` table.

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

Tests modify rows in `try/finally` blocks and restore the original value.
If a test crashes mid-flight, one setting value may be left modified —
recover by re-running the §16 INSERT block from `data-model.sql` or fixing
the affected key by hand. The current probe key is
`AI.Polling.IntervalMinutes` (original value: `5`).

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
