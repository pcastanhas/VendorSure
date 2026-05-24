# LessonsLearned.md

> Things we figured out the hard way. Mistakes that cost time. Surprises in
> libraries or tooling. The kind of context that's normally lost in chat
> history.
>
> Entries are short, dated, and keep the "why" along with the "what." A
> lesson without the why turns into folklore.

## Format

Each entry:

```
## YYYY-MM-DD — short title

What we hit, what we learned, what we'll do differently.
```

## Entries

## 2026-05-23 — .NET 10 emits `.slnx`, not `.sln`

`dotnet new sln` on .NET 10 creates a `VendorSure.slnx` file, not the
classic `VendorSure.sln`. The new format is XML-based and is the default
going forward. `dotnet build` / `restore` / `test` accept it transparently,
but you have to call it by the right name (`dotnet build VendorSure.slnx`)
since auto-detection looks for both. README and BUILD.md reference the
`.slnx` filename.

## 2026-05-23 — Sandbox can't restore NuGet packages

The agent's sandbox doesn't have outbound access to `api.nuget.org`.
Consequence: the agent can author project files, source code, and template
shapes, but cannot run `dotnet build` end-to-end to verify. The first
build of every chunk happens on the developer's machine after `git pull`.
Round-trip is: agent commits → developer pulls, builds, reports
errors/output → agent fixes. This is captured in BUILD.md and is the
working model for all subsequent chunks.

## 2026-05-23 — DI singleton ctors are not "fail fast at startup"

In Chunk 3 I almost shipped a `SqlConnectionFactory` that validated the
connection string in its constructor, on the theory that this would fail
fast at startup if the string was missing. It would have — but in a way
that defeats the "app boots either way" requirement of Chunk 3, because
the DI container resolves singletons lazily on first request, and the
first request happens during the startup hosted-service's *construction*,
before that service's own try/catch can guard against it. Net effect: a
missing connection string would crash the host before the reachability
check could log anything.

Fix: resolve the connection string inside `CreateOpenConnectionAsync`
instead. Now a missing string only throws when something actually asks for
a connection. The reachability check's existing try/catch in `StartAsync`
catches it, logs the error, and `StartAsync` returns cleanly so the host
finishes booting. The app comes up; the operator reads the log; they fix
the configuration.

Lesson: if you want a startup health surface that "logs but doesn't
crash," the DI graph that feeds it must also be tolerant of bad config —
otherwise the construction failure happens before any try/catch you wrote
can run. Lazy resolution at the lowest level is the cleanest answer.

## 2026-05-23 — Compaction summaries can lag the actual repo state

When a long session is compacted into a summary and the agent picks up
again, the summary is a snapshot taken at compaction time — not
necessarily the moment work last stopped. In one Phase 1 session the
summary described Chunk 5 as "started, not yet committed" with a list of
files staged for commit. After compaction I sat down to finish Chunk 5
and found those files already existed on disk and the chunk was
committed *and pushed* (`99f830c`) — work that happened between the
summary being generated and the session ending. I caught it before
duplicating any code, but only after re-reading files that were already
in place.

Lesson: in any continuing session, **run `git log --oneline -10` and
`git status` before trusting the compaction summary's claims about
what's committed or pending.** The summary describes intent; git
describes reality. When they disagree, git wins. This is cheap to do
and prevents the worst failure mode (rewriting code that was already
shipped, then forcing a merge or — worse — overwriting good commits).

## 2026-05-23 — Blazor Web App interactive render mode must cascade

In Chunk 7 the Settings list page had `@rendermode InteractiveServer`
at the top, the pencil-edit button had an `OnClick` handler, and the
button did nothing when clicked. No errors, no log entries, no
exceptions — the page felt frozen. The SignalR circuit was being
negotiated (visible in the request log: `/_blazor/negotiate` followed
by `CONNECT /_blazor`) but click events were never reaching the
server.

The trap: in .NET 8+ Blazor Web Apps, **`@rendermode` declared on a
page only makes that single component interactive — the layout
chain above it (MainLayout, App, etc.) stays statically rendered**.
MudBlazor's services (DialogService, Snackbar, KeyInterceptor,
PopoverService) are provided by `MudDialogProvider`,
`MudSnackbarProvider`, etc., which sit inside MainLayout. When the
layout is static-rendered, those providers can't run interactively
either. The page below them ends up half-rendered: the markup is
present, but the JS plumbing that wires up event dispatch to MudBlazor
components is dead. Pencil button → click → silence.

Fix: move `@rendermode="InteractiveServer"` up to the routing
infrastructure in `App.razor`, where it cascades through the entire
component tree:

```razor
<HeadOutlet @rendermode="InteractiveServer" />
<Routes @rendermode="InteractiveServer" />
```

With that change every page, layout, and provider in the app is
interactive — no per-page `@rendermode` needed, no surprise dead
zones. The per-page directives can be removed (they're redundant; in
some configurations they conflict).

For VendorSure this is fine: the entire app is admin-tooling for 5
power users with no public surface, so there's nothing to gain from
mixing static and interactive rendering. Set it once, everywhere, done.

## 2026-05-23 — Cross-table business rules belong in repository SQL (so far)

Three patterns settled in Phase 2 that future code should follow until
they stop working. Recording all three together because they're parts
of one philosophy: keep business rules close to the data, expressed in
the type system, not in exceptions.

**1. Rules go in the mutating statement's WHERE clause.** When the
rule is "X can't happen if Y exists in another table," it's tempting
to do a check query, then run the mutation. That has a race window
between the check and the mutation. Instead, embed the check directly:

```sql
UPDATE dbo.user_groups
SET name = @Name, is_active = @IsActive, ...
WHERE id = @Id
  AND (@IsActive = 1
       OR is_active = 0
       OR NOT EXISTS (SELECT 1 FROM dbo.users WHERE group_id = @Id));
```

If the rule rejects, `rowsAffected = 0`. To distinguish "row didn't
exist" from "rule rejected," run focused follow-up probes — one per
possible rejection reason, in specificity order. Two queries instead of
one, but the rule itself is atomic.

**2. The rule fires only on the relevant transition, not on no-ops.**
The first cut of the user-group rule was "block any UPDATE with
IsActive=0 when users are assigned." This breaks a perfectly normal
flow: rename an already-inactive group. Fix is to add `OR is_active = 0`
to the rule predicate — if we're not transitioning active→inactive,
the rule doesn't apply. *Always think through what the rule does for a
no-op write before committing the SQL.* Regression-tested.

**3. Multiple expected outcomes → result enum, not exceptions.** When
an operation has multiple expected failure modes (RejectedEntraidConflict,
RejectedInactiveGroup, NotFound), each one is a thing the caller will
want to react to — typically with a different UI message. Exceptions
for these would be code smell (exceptions are for unexpected things).
Return an enum (or a small record carrying the enum + new id for
creates). The UI switches on it and shows the right snackbar.

**Why not a service layer with the rules?** When I added the first
cross-table rule (Chunk 1) I noted that a second rule would trigger
factoring into a `UserGroupService`. When I shipped the second rule
(Chunk 2) I reconsidered: each rule is a one-statement SQL check that
lives naturally with its mutation; a service class with two delegating
methods adds friction with no benefit. **The trigger for a service is
a rule that needs orchestration across multiple writes, or one that
can't be expressed in SQL** — not "we have N ≥ 2 rules." Deferred,
explicitly. Revisit in Phase 4-5 when validation runners and workflow
state may need genuine orchestration.

## 2026-05-23 — MudBlazor 9.0 renamed `ShowMessageBox` → `ShowMessageBoxAsync`

When writing the Required Documents delete confirmation in Phase 3 /
Chunk 2 I reached for the idiomatic `await
DialogService.ShowMessageBox(...)` pattern, which is what every
tutorial and answer dating from MudBlazor 6/7/8 shows. In MudBlazor
9.0 the method was renamed to `ShowMessageBoxAsync` (PR #12292,
breaking change). The old name is gone — calling it is a compile
error, not a deprecation warning.

I caught it before committing by web-searching the API while writing
the code, but only because something nudged me to double-check.
Without that check it would have been a round-trip: agent commits →
developer pulls, build fails, reports back → agent fixes. The shape of
the failure ("method not found on DialogService") is easy to diagnose
once you see it; the cost is the round trip.

**Lesson, two parts:**

1. MudBlazor 9.x renamed several legacy synchronous-looking methods to
   `*Async` for consistency. If pre-9.0 examples are the only thing I
   can recall for an API, **search before using it** — assume
   something renamed. Other affected names from the 9.0 release notes
   that may bite us later: `MudDialogContainer.OnMouseUp` →
   `OnMouseUpAsync`, MudFormComponent's various Reset / Touched
   methods. Worth a search whenever I'm reaching for a method I
   haven't actually called in this codebase yet.

   **Update during Phase 4 / Chunk 5:** two more renames caught
   pre-commit while building the Request Type detail page:
   `MudTabs.PanelClass` → `TabPanelsClass` (and `TabPanelClass` →
   `TabButtonsClass`); `MudGrid` does NOT have an `AlignItems`
   parameter (it's `MudStack` that does — `MudGrid` only has
   `Spacing` and `Justify`). The pattern is the same as Lesson #7:
   muscle memory from a different version or a different MudBlazor
   container suggests an API that doesn't exist on this one. The fix
   for both was a quick API-surface search before commit, saving a
   round-trip. **Don't trust generic familiarity with the framework;
   verify the API surface on the exact component on the exact version.**

2. The rest of the dialog API is unchanged in 9.x:
   `DialogService.ShowAsync<T>(title, parameters, options)`,
   `DialogResult.Ok(data)`, `DialogResult.Canceled`, `DialogOptions`,
   `DialogParameters<T>`. So when in doubt, follow the shape of
   `EditUserDialog`/`EditUserGroupDialog`/`EditDocumentTypeDialog` —
   those compile and work on 9.4.

## 2026-05-23 — Don't rely on transitive NuGet references

The Infrastructure.Tests project called `services.AddLogging()` in its
fixture but didn't reference `Microsoft.Extensions.Logging` — only the
abstractions package (transitively, via the Infrastructure project). At
some point that transitive resolution went away and the test project
stopped compiling with:

```
CS1061: 'ServiceCollection' does not contain a definition for
'AddLogging' and no accessible extension method 'AddLogging' …
```

The fix is one line in the test csproj — make the package reference
explicit. But the principle is broader: **if code in a project calls an
extension method, that project must reference the package the
extension is in**, even if it builds today via some transitive path.
NuGet's PrivateAssets/IncludeAssets defaults and 9-to-10 patch bumps
both routinely drop transitive references that used to work.

When a chunk's CI build fails with "method X doesn't exist on type Y"
and the method is a Microsoft.Extensions.* extension, first guess:
the project doesn't reference the concrete package, only the
abstractions one. Specifically:

- `Microsoft.Extensions.Logging.Abstractions` → has `ILogger<T>`,
  `LogLevel`, but NOT `AddLogging`.
- `Microsoft.Extensions.Logging` → has `AddLogging`, the host of
  extension methods, etc.
- `Microsoft.Extensions.DependencyInjection.Abstractions` → has
  `IServiceCollection`, `ServiceLifetime`, but NOT `AddScoped`/
  `BuildServiceProvider`/etc.
- `Microsoft.Extensions.DependencyInjection` → has those.

Rule of thumb: production code can reference *only the abstractions*
to keep coupling low. Host code (Program.cs, test fixtures) needs the
concrete packages.

## 2026-05-23 — Same-version invariant via INNER JOIN

The `request_type_validation_documents` junction (Chunk 3) attaches a
validation row to a required-document row. The schema's FKs on each
column only enforce that the referenced rows exist; they don't enforce
that both belong to the same Request Type version. But same-version is
a real invariant — a validation looking at documents from a foreign
version makes no sense.

The cleanest way to enforce it is at the INSERT, in one statement:

```sql
INSERT INTO dbo.request_type_validation_documents (...)
SELECT @validationId, @requiredDocId
WHERE EXISTS (
    SELECT 1
    FROM dbo.request_type_validations val
    INNER JOIN dbo.request_type_required_documents rd
        ON rd.request_type_version_id = val.request_type_version_id
    INNER JOIN dbo.request_type_versions ver
        ON ver.id = val.request_type_version_id
    WHERE val.id = @validationId
      AND rd.id = @requiredDocId
      AND ver.request_state = @draftCode);
```

The `INNER JOIN ... ON rd.request_type_version_id = val.request_type_version_id`
**is** the same-version check. The join produces a row exactly when:

- both endpoints exist (otherwise the joined rows wouldn't be there
  for the EXISTS to find), AND
- they share a `request_type_version_id` (otherwise the JOIN's ON
  predicate doesn't match).

No separate equality check, no extra round-trip. The schema's FKs
handle existence; the JOIN handles same-version-ness. When two
existence checks AND a same-row check have to combine into one
atomic gate, an INNER JOIN with both endpoints in the WHERE is the
right shape. Worth remembering for any cross-junction invariant
the schema can't express directly.

## 2026-05-23 — UPDLOCK for atomic read-then-update

The Phase 4 / Chunk 9 `TransitionToInServiceAsync` is the first method
to mutate `request_state` after the initial Draft seed. Promotion is a
multi-row operation: the target Draft moves to In Service, and the
prior In Service (if any) moves to Superseded. Both happen in one
transaction.

The naïve shape — `SELECT type_id FROM ... WHERE state = 'D'` then
two `UPDATE` statements — has a race window:

- Caller A reads version X's type id, finds it's Draft on type T.
- Caller B reads X's type id concurrently, also finds it's Draft on T.
- A's UPDATEs run: prior InService of T → Superseded, X → InService.
- B's UPDATEs run: prior InService of T (now X, just promoted) →
  Superseded, X → InService (idempotent).
- Net result: B has demoted A's just-promoted version. Wrong.

Fix: serialise with `UPDLOCK` on the initial SELECT.

```sql
SELECT request_type_id
FROM dbo.request_type_versions WITH (UPDLOCK, ROWLOCK)
WHERE id = @versionId AND request_state = 'D';
```

`UPDLOCK` holds an exclusive lock on the row for the duration of the
transaction, blocking any other transaction trying to do the same
read. Caller B blocks on this SELECT until A commits, then re-reads
the row and finds `request_state != 'D'` (A's UPDATE already moved
it). B's SELECT returns no row, B returns `RejectedNotDraft`. No
race.

When to reach for UPDLOCK: any "check then mutate" sequence within a
transaction where the check determines whether the mutation should
proceed AND concurrent callers could each pass the check independently.
Alternatives like `SERIALIZABLE` isolation work but are coarser
(everything in the transaction holds locks); `READ COMMITTED SNAPSHOT`
doesn't help because the issue isn't dirty reads, it's that both
readers see the same valid state and then both write. A schema-level
filtered unique index (e.g., `UNIQUE (request_type_id) WHERE
request_state = 'I'`) would also enforce at-most-one-InService and
turn the race into a constraint violation; that's a more durable
solution and worth considering if more transition rules show up.
For now, `UPDLOCK` on the read is the minimal change with the right
semantics.

Pair the lock with a single C#-captured timestamp passed to both
UPDATEs (instead of `SYSUTCDATETIME()` in each) so the new
`placed_in_service_ts` equals the prior `superseded_ts` exactly —
makes the audit story line up nicely when reading the version log
chronologically.

## 2026-05-23 — Pre-commit MudBlazor API-surface check is worth the minute

Phase 4 caught **three** MudBlazor 9.x API bugs at the "let me search
the docs for this attribute" step, before commit:

- `MudTabs.PanelClass` → renamed to `TabPanelsClass` in v9.0 (Chunk 5).
- `MudGrid` does not have an `AlignItems` parameter (that's `MudStack`)
  — `MudGrid` only has `Spacing` and `Justify` (Chunk 5).
- (Also: `MudCheckBox.Value/ValueChanged` was renamed from
  `Checked/CheckedChanged` in v7 — confirmed before use in Chunk 6.)

The common shape: I wrote markup from muscle memory or recall, and
the syntax LOOKED right because it matched analogous components or
older versions. A 60-second web search before commit avoided a round-
trip on each. **The check is cheap; the round-trip isn't.** Specifically
worth searching when:

- Reaching for an attribute on a component I haven't actually called
  in this codebase yet.
- Generalising from one component's API to a sibling's (e.g.
  "MudStack has AlignItems, MudGrid probably does too" — wrong).
- Using a method or attribute by recall after a major-version jump
  (MudBlazor 6 → 7 → 9 each had renames).

Lesson #7 (the original `ShowMessageBox` → `ShowMessageBoxAsync`
rename) was the first instance; Phase 4 made the pattern explicit
and worth its own lesson. The catalog of catches lives in #7's body;
future Phase 5+ catches should land there too rather than
proliferating into new entries.

## 2026-05-23 — When delete is multi-row, transaction; when add/update is multi-row, conditional

Two transactional-write patterns emerged in Phase 4 and they look
different on purpose.

**Pattern A — multi-row DELETE: explicit transaction.** Chunk 3's
`RequestTypeValidationRepository.DeleteAsync` and Chunk 9's
`TransitionToInServiceAsync` both write to multiple rows, and both
use `BeginTransaction()` + `transaction.Commit()` / `Rollback()`.
The reasoning: each statement is independent; if the connection
drops between them, the half-applied state is bad (orphan junction
rows, or a promoted-but-not-demoted version). The transaction
boundary guarantees all-or-nothing.

**Pattern B — multi-condition INSERT/UPDATE: conditional WHERE.**
Chunk 2's `AddAsync` on the required-docs junction enforces four
rules (version exists, version is Draft, library exists, pair isn't
already attached) in one statement with `INSERT ... SELECT ... WHERE
EXISTS (...) AND EXISTS (...) AND NOT EXISTS (...)`. No transaction
needed — one statement is atomic by default. If any condition fails,
zero rows insert and `SCOPE_IDENTITY` returns NULL.

The distinction is: Pattern A's atomicity requirement is across
*multiple statements*; Pattern B's is across *multiple conditions
within one statement*. Reach for a transaction only when the unit of
atomicity actually spans statements. A single conditional statement
gives atomicity for free.

Corollary: when Pattern B's "zero rows" case needs to be
disambiguated (RejectedDuplicate vs RejectedNotDraft vs
RejectedVersionNotFound), follow up with focused probes outside the
transaction, in specificity order. The probes can race with their own
window but the answer doesn't matter — "X doesn't exist anymore" and
"X exists but state is wrong" are both legitimate observations of
the post-failure world.

## 2026-05-24 — D3.js: vendor locally, load UMD via script tag, render in OnAfterRenderAsync

Phase 5 / Chunk 5. First JS interop in the codebase. Lessons:

**Vendor over CDN.** The agent's sandbox couldn't reach `cdn.jsdelivr.net`
(egress allowlist blocks it). The user's dev environment is internal and
might be similarly constrained behind a corporate firewall. The npm
registry was reachable (`registry.npmjs.org` is on the allowlist), so we
pulled `d3@7.9.0` and committed `wwwroot/lib/d3.v7.min.js` (~280KB).
This pattern is worth following for any future third-party JS: if it's
not already vendored, prefer pulling the tarball from npm to declaring a
CDN reference. CDN convenience isn't worth a brittle dev experience for
the user, and 280KB once in git is fine.

**UMD loaded via <script>, module logic as ES module.** D3's distribution
file is a UMD bundle — it sniffs the environment and attaches to `window.d3`
when there's no module loader. ES `import` of the UMD file doesn't give
you a usable export, so the practical pattern is:
  - `<script src="lib/d3.v7.min.js">` in App.razor body — exposes
    `window.d3` globally before any Razor page initializes.
  - The page-specific JS file is an ES module that reads `window.d3`
    and exports its own functions. The page imports it via
    `IJSRuntime.InvokeAsync<IJSObjectReference>("import", "./js/...")`.

This gives the best of both: ES module discipline for our code, simple
script-tag inclusion for the third-party library.

**Mount in `OnAfterRenderAsync`, guard on data-ready flags.** The first
`OnAfterRenderAsync(firstRender: true)` fires after the FIRST render —
which, for a page with an async `OnParametersSetAsync`, is the loading-
spinner render. The canvas div doesn't exist yet. Don't gate on
`firstRender` alone. Gate on the data-ready flag (`!_loading &&
_loadError is null && _workflow is not null`) plus a `_mounted`
boolean to prevent re-mount on subsequent renders. Subsequent
`OnAfterRenderAsync(firstRender: false)` calls are how the mount
actually happens.

**Reset `_mounted` in LoadAsync to support param changes.** If the
component is reused for different route params (same page, new
WorkflowId), `OnParametersSetAsync` runs again. Setting `_mounted =
false` at the top of LoadAsync, combined with `@key="_workflow.Id"`
on the canvas div, ensures the JS module is re-invoked with the new
graph data after the new load completes. Without the reset, you get
a stale canvas pointing at the old workflow.

**Build the JS-friendly payload in C#, not JS.** The repo returns
`WorkflowNode` records with .NET-cased properties and FK-style edges
(path1/path2). Transforming this into `{nodes: [...], edges: [...]}`
in C# before invoking `mount` keeps the JS module dumb: it never has
to know domain rules ("path2 is only valid on Decisions") because
the C# side has already flattened the structure. The JS module just
draws whatever it's handed.

**JSDisconnectedException is the friend, not the foe.** When the
SignalR circuit drops (user navigated away, network blip), any
pending JS call throws `JSDisconnectedException`. Catch it
quietly in both the mount path and the disposal path — there's
nothing meaningful to clean up because the browser already disposed
the document. The Logger call would otherwise generate noise on
every successful page-away.

## 2026-05-24 — HTML5 drag-and-drop in Blazor: setData() needs raw JS, MudPaper isn't reliable for drag sources

Phase 5 / Chunk 6. Adding the palette → canvas drag to create
nodes. The first JS→Blazor callback in the codebase. Several
gotchas:

**Blazor's `DragEventArgs.DataTransfer` is read-only.** The C#
type exposes `Types`, `DropEffect`, `EffectAllowed`, etc., but
no `SetData()` method. So `@ondragstart="..."` can't put a
payload onto the drag — it can only react to the event after
the browser already created it. The dataTransfer object Blazor
hands you is a server-side projection of the browser's, captured
at the moment the event fired. Mutating it would have nowhere to
land.

The workaround is to use raw HTML `ondragstart` (no `@`), which
is an inline-JS attribute the browser executes natively during
the actual dragstart event. The C# code computes the payload
string and emits it into the markup:

```razor
<div draggable="true"
     ondragstart="@DragStartScript(nodeTypeId, blockCatalogId)">
```

Where `DragStartScript` returns a fragment of JS that calls
`event.dataTransfer.setData('application/json', '...')`. The
drop side stays pure JS too (the workflow-designer module's
drop listener calls `event.dataTransfer.getData(...)`), so the
whole drag-data channel is native — Blazor only sees the result
after the drop, via a `DotNetObjectReference.invokeMethodAsync`
call from JS.

**Side note about Blazor's `@ondrop`.** Even when you give up on
`@ondragstart`, `@ondrop`'s `DragEventArgs.DataTransfer` is
reportedly often empty (dotnet/aspnetcore#43976). The reliable
path is JS on both sides of the drag, with Blazor only entering
the picture via a `[JSInvokable]` method after the drop completes.

**MudPaper isn't reliable for drag sources.** MudBlazor has
known issues with attribute splatting (issues #1843, #5437,
#7796) — `UserAttributes` and inline HTML attributes don't
always propagate to the rendered element. For a drag source, we
need `draggable="true"` AND `ondragstart="..."` to land verbatim
on the rendered `<div>`, and the safest way is to write a plain
`<div>` directly. We can still use MudBlazor styling classes
(`mud-paper`, `mud-paper-outlined`, `pa-2`, etc.) on the div —
no need to wrap it in `<MudPaper>`. Less coupling to MudBlazor
quirks, identical visual result.

**`DotNetObjectReference` lifecycle.** Page-scoped: create lazily
in `OnAfterRenderAsync` when the mount needs it; dispose in
`DisposeAsync`. Failing to dispose leaks the reference (JS retains
a handle to the Blazor circuit's view of the component, preventing
GC of the page's state).

Two refinements worth holding onto:
- Allocate the dotnet ref **only when there's a write path**. We
  skip it when the version is read-only, and the JS module's
  attach-listeners guard (`if (!entry.listenersAttached &&
  entry.dotNetRef)`) honors that — no drop listeners, no wasted
  ref.
- Pass the ref on every `mount()` call. The JS module updates
  its per-canvas state but only attaches listeners once
  (idempotent — `entry.listenersAttached` flag). Re-mounts after
  a successful create still work because the listeners persist
  across `innerHTML = ""` (which clears children but not
  listeners on the container itself).

## 2026-05-24 — Auto-Start in workflow create + MudBlazor popover positioning is fragile

Phase 5 / Chunk 7. A design pivot replaced Chunk 6's free-drag
palette with a +-button graph-construction model rooted at Start.
Several patterns worth recording from the implementation:

**Auto-Start lives inside the workflow create transaction.** Every
workflow now has a Start node by invariant. Two ways to enforce
this:
  - At every caller site: each caller manually creates Start after
    creating the workflow. Repetitive and easy to forget.
  - At the repo surface: `WorkflowDefinitionRepository.CreateAsync`
    inserts the workflow row AND the Start node AND updates
    `start_node_id`, all in one transaction.

The repo-level approach wins because the invariant ("workflow ⇒
Start") becomes a property of the data layer that every caller —
present and future — gets for free. The engine, future export
tools, audit reports — none of them have to remember "Start may
be null." The schema's `start_node_id` field is nullable because
SQL Server can't natively express "FK that must reference a row
that doesn't exist yet at insert time"; semantically Start is
mandatory and the repo guarantees that.

Atomic across two statements requires an explicit transaction.
The probe pattern (run "why was this rejected" queries after a
no-op INSERT) had to move outside the transaction since we roll
back before probing — the probes look at the post-failure world,
not the transactional one.

**`InsertChildAsync` is a new high-level operation, not an
extension of CreateAsync.** Considered both. The old `CreateAsync`
created orphan nodes (level=0, no path FKs) for the Chunk 6 free-
drop model. The new operation atomically:
  1. inserts the new node at parent.level + 1,
  2. wires the parent's slot to point at it,
  3. if the slot had a previous child, wires that as the new
     node's child (splice/insert-between),
  4. renumbers the displaced subtree via the existing recursive
     CTE.

Argument: extend CreateAsync with optional parent + slot
parameters. Verdict: rejected. The semantics are different enough
(orphan vs splice, no FK touched vs FK rewiring, no renumber vs
recursive renumber) that overloading one method obscures the
distinction at call sites. A new method makes the high-level
operation legible. `CreateAsync` stays as the leaf-create
primitive — used by 50+ existing tests and a viable building
block for future "import workflow" features.

**Renumbering the displaced subtree composes for free.** Insert-
between case: new node N takes level L; the displaced child C and
all its descendants shift down by 1. The existing
`RenumberSubtreeAsync(rootNodeId, rootLevel)` walks via path1/
path2 setting each descendant to parent.new_level + 1. Calling it
with `(newNodeId, parent.level + 1)` produces:
  N at L (new wired into parent's slot)
  C at L+1 (it's N's child via path1 or pathN)
  C's descendants at L+2, L+3, ...

Exactly the shift-by-1 behavior we need. The CTE was written for
SetPath wiring; insert-between is a natural composition with no
new SQL.

**Strip the new invariant in test fixtures rather than rewrite
50+ tests.** Auto-Start means every workflow created via the repo
has one node already. Existing `WorkflowNodeRepositoryTests`
assume a clean "no nodes" workflow and build their own graphs
starting with their own Start. With auto-Start they'd see an
extra row.

Two options:
  - Rewrite every test to account for auto-Start (50+ sites).
  - Strip auto-Start in `SetupAsync` via raw SQL (one helper).

The fixture-level fix wins because:
  - 50+ tests already pass against the old contract; they're
    correct tests of node-repo behavior in isolation.
  - The new behavior (auto-Start) is a property of
    `WorkflowDefinitionRepository`, not `WorkflowNodeRepository`.
    Tests of the node repo shouldn't care.
  - The strip is a one-line raw-SQL helper called in `SetupAsync`,
    bypassing the Draft gate so the fixture works whether or not
    a test forces the version into a non-Draft state.

The new contract is covered by tests in
`WorkflowDefinitionRepositoryTests` instead.

**MudBlazor popover positioning at JS-supplied coordinates is
fragile; use a centered dialog.** Originally planned: render a
`MudPopover` anchored near the + button the user clicked. JS
sends `clientX` / `clientY` to Blazor; Blazor positions a hidden
anchor div at those coordinates; popover anchors to it.

Reality: MudBlazor 9 uses a portal pattern. `MudPopoverProvider`
at the layout root renders all popover content there, regardless
of where the popover is declared. Positioning is computed by
MudBlazor's own JS using `AnchorOrigin`, `TransformOrigin`, and
the anchor element's bounding rect. Trying to override via inline
`Style="top:...; left:...;"` fights MudBlazor's positioning JS
and produces unreliable results (see MudBlazor issues #3241,
#7451 for the genre).

Switched to a centered `MudDialog`. Less precious UX but
mechanically rock-solid: dialog renders in the dialog provider,
centered, no positioning logic needed. Perfectly fine for a
5-power-user internal tool. The `OnPlusClickedAsync` callback
still accepts `clientX` / `clientY` from JS for forward-
compatibility — if a future variant wants the popover UX, the
plumbing is half-built — but the current dialog discards them.

**MudPaper for drag sources is unreliable, but plain `<div
class="mud-paper mud-paper-outlined">` is fine.** Carried over
from Chunk 6's lesson, but worth restating in the new context:
when you need raw HTML attributes (`draggable`, `ondragstart`,
or in this chunk's case, inline `@onclick` with parameters), use
a plain `<div>` and apply MudBlazor's utility classes manually.
You get the visual styling without coupling to MudBlazor's
sometimes-quirky attribute-splat behavior.

**General principle this chunk reinforced.** When the picture
matches but the dependencies are fighting you, simplify the
picture. The popover-near-click UX was nicer-sounding but the
plumbing cost was high. The centered dialog gets 80% of the
benefit at 10% of the friction. Same with the `CreateAsync` vs
`InsertChildAsync` decision: keeping the primitive separate from
the high-level op gave both clear names and clear semantics,
where overloading one would have made every call site harder to
read.
