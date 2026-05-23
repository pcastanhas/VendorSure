# BUILD.md

> How to build, run, and test VendorSure locally. This document grows as the
> app gains capabilities. If something here is stale, fix it in the same
> commit that broke it.

## Status

No code yet. Phase 1 / Chunk 1 will add the solution structure and this
document's first real content.

## Prerequisites (anticipated)

- .NET 10 SDK.
- A SQL Server instance reachable from your dev machine. Local Express,
  remote dev box, or container — all fine.
- The `VenSure` database created and `docs/data-model.sql` applied against it.

## Configuration (anticipated)

Coming with Phase 1 / Chunk 1.

## Build (anticipated)

```bash
dotnet build VendorSure.sln
```

## Run (anticipated)

```bash
dotnet run --project src/VendorSure.UI
```

## Test (anticipated)

```bash
dotnet test
```

## Logs (anticipated)

Serilog writes to `logs/app-YYYY-MM-DD.log` in the working directory, rolling
daily, 30-day retention.

## Schema changes

`docs/data-model.sql` is the source of truth for the schema. When the schema
changes, edit this file and apply the changes manually to your dev DB. There
is no migration runner; this is intentional (single-developer, single-dev-DB,
no production deploy in scope).
