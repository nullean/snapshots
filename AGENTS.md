# Nullean.Snapshots: contributor and agent conventions

Snapshot testing on AwesomeAssertions for text, HTML, JSON and YAML, plus a native AOT CLI, `snap`.
[DECISIONS.md](DECISIONS.md) records the non-obvious choices; read it before changing where files go or how they are
named.

## Build and test

```sh
dotnet build -c Release
dotnet test -c Release
dotnet test -c Release --project tests/Nullean.Snapshots.Tests --treenode-filter "/*/*/SnapshotTextTests/*"
```

Requires the .NET 10 SDK (pinned in `global.json`). Tests run on Microsoft.Testing.Platform with TUnit.

## Rules

- Failure messages are plain text: no ANSI, repo-relative paths, capped, readable by people and agents.
- Never pass snapshot content as a format string; braces in content must not break a message.
- Core has no heavy dependencies. Libraries set `IsAotCompatible`; an exception is documented in DECISIONS.md.
- The diff engine is tested with CsCheck properties first. Keep a failing seed as a regression test.
- Never accept snapshots automatically, and never in CI.
- Never publish packages or push tags without the owner's approval.
- Record non-obvious choices in DECISIONS.md in the same change.
