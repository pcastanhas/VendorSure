# BUILD.md

> How to build, run, and test VendorSure locally.

## Prerequisites

- **.NET 10 SDK** (10.0.107 or later)
- A **SQL Server** reachable from your dev machine. Local Express, remote
  dev box, or container — all fine. The DB name should be `VenSure` to
  match what `data-model.sql` expects.
- The `VenSure` database created and `docs/data-model.sql` applied against
  it (covered in later chunks once the app actually needs DB access — for
  Chunk 1a, the DB isn't read or written).

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

3. Restore packages and build:

   ```
   dotnet build VendorSure.slnx
   ```

   First restore takes a minute or two (MudBlazor is a few MB).

## Run

```
dotnet run --project src/VendorSure.UI
```

Browse to the URL the launcher prints (typically `https://localhost:7298`).
You should see a MudBlazor shell: top app bar reading "VendorSure", a left
nav drawer with menu items (Home, Settings, Users, etc.), and the home
page in the content area.

## Test

```
dotnet test VendorSure.slnx
```

There are no tests yet (Chunk 1a adds no asserts). The test projects exist
and `dotnet test` should report zero failures.

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
hosted services run during `app.Run()` so the DB line may interleave with
the ready line):

```
[HH:MM:SS INF] VendorSure UI starting up
[HH:MM:SS INF] VendorSure UI ready — environment Development
[HH:MM:SS INF] Connected to VenSure database (server=..., database=VenSure)
```

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
