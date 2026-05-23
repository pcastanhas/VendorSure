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
