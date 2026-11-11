---
name: nullean-snapshots
description: >-
  Use when a .NET test compares output with a stored snapshot: accepting new or changed snapshots, scrubbing volatile
  values, or finding where verified and received files live. Nullean.Snapshots is the store and workflow behind every
  Nullean.Snapshots assertion package.
---

# Nullean.Snapshots

The core: it keys a snapshot by test, writes `.verified.*` and `.received.*` files, and decides when to accept. Tests
normally call it through `Nullean.Snapshots.AwesomeAssertions` (`MatchSnapshot`) or a format package
(`.Json`, `.Yaml`); call `Snapshot.Match` directly only to build your own assertion.

## The calls you need

`using Nullean.Snapshots;` Scrub volatile values for every snapshot, once, before tests run:

```csharp
[ModuleInitializer]
internal static void Init()
{
    SnapshotDefaults.AddScrubber(Scrubbers.Guids);     // Guid_1, Guid_2, ...
    SnapshotDefaults.AddScrubber(Scrubbers.DateTimes); // DateTime_1, ...
}
```

Scrub for one call, or skip the global scrubbers:

```csharp
var options = new SnapshotOptions().Scrub(new Regex(@"\d+ ms"), "<n> ms");
var raw = new SnapshotOptions { IgnoreDefaultScrubbers = true };
```

Your own assertion on top of the store:

```csharp
var result = Snapshot.Match(actual, SnapshotFormats.Text, new SnapshotCallSite(file, member, line));
if (!result.Passed) throw new Exception(result.FailureMessage);
```

Accept, after reading the received file:

```sh
snap accept MyTests/Renders                  # CLI, Nullean.Snapshots.Cli
SNAPSHOT_ACCEPT=1 dotnet test --project tests/App.Tests
```

## Gotchas

- A new snapshot fails: with no verified file the test fails and writes a received file.
- `SNAPSHOT_ACCEPT=1` (or `true`) accepts every mismatch in the run, not only new snapshots. Narrow it with
  `--treenode-filter`, as the failure message shows.
- With `CI` set to `1` or `true`, `SNAPSHOT_ACCEPT` is ignored and the message says so. Accept locally.
- Verified: `.snapshots/<test file dir>/<Class>/<Method>[.<name>][.<argsHash>].verified.<ext>`; commit them.
  `<Class>` is the test file name unless `Nullean.Snapshots.TUnit` supplies the runtime class.
- Received: `$(ArtifactsPath)/.snapshots/received/<Project>/...`, else `obj/snapshots/received/`.
- Two snapshots in one test throw `InvalidOperationException` ("pass a name"). A `name` may hold only letters, digits,
  `-`, `_` and inner `.`, or it throws `ArgumentException`.
- No scrubber runs by default. Global scrubbers run before per-call ones.
- Text is stored as UTF-8 without BOM, LF endings, one trailing newline; the rest compares exactly.
- Accepting an inline snapshot edits the string literal in the test source, so it must be a literal, not a constant
  or interpolation. Its received record is `<key>.L<line>.received.inline`.
- Moving a test file orphans its snapshots; `snap orphans` lists them. Override the folder with `SnapshotsDirectory`.

Docs: https://snapshots.nullean.net/reference/layout
