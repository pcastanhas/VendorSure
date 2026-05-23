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
