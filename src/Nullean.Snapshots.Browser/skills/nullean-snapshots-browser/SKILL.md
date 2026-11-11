---
name: nullean-snapshots-browser
description: >-
  Use when C# code needs to run a browser journey (a `.journey` file of agent-browser commands) in Chrome and compare
  what it read with a verified snapshot. Nullean.Snapshots.Browser runs journeys with Nullean.Cdp and compares their
  transcripts and accessibility trees as trees.
---

# Nullean.Snapshots.Browser

`using Nullean.Snapshots.Browser;` runs a journey and returns a `JourneyTranscript` you compare with
`JourneySnapshot.Match`.
In AwesomeAssertions tests use Nullean.Snapshots.Browser.Assertions (`JourneyTest.RunAsync`) instead; for a suite in a
console tool, Nullean.Snapshots.Browser.Console.

## The calls you need

A journey, one command per line; reads (`snapshot`, `get`, `is`) become transcript sections:

```text
open index.html
find role button click --name "Add red lamp"
snapshot -c -s "#results"
get text #count
```

Run it and compare without a test framework:

```csharp
var options = new JourneySnapshotOptions { BaseUrl = new Uri("http://localhost:5000/") };
var transcript = await JourneySnapshot.RunAsync("journeys/shop.journey", options);
var result = JourneySnapshot.Match(transcript);
if (!result.Passed) throw new InvalidOperationException(result.FailureMessage);
```

A live site: no comparison, no writes, skip what does not apply:

```csharp
var live = new JourneySnapshotOptions { BaseUrl = site, Transcript = TranscriptMode.Off, ReadOnly = true };
if (JourneySnapshot.SkipReason("journeys/shop.journey", live) is null)
    await JourneySnapshot.RunAsync("journeys/shop.journey", live);
```

List journeys for a data source: `JourneySnapshot.Find("journeys")`.

Two sites: `JourneySnapshot.CompareAsync(file, new JourneyCompareOptions { Baseline = prod, Target = preview })` runs a journey
against both and compares under the rules in `{journey}.differences` (the `snap journey compare` command does the same and can
`--record` rules). A rule is `added link "x"`, `removed text "a line"`, `changed heading "a" -> "b"` or `ignore url` under a
`## step` heading; `include?` pulls in a file of one preview's differences. `screenshot` steps are compared too, with a diff
image. Rule reference: https://snapshots.nullean.net/workflow/expected-differences

## Gotchas

- The snapshot belongs to the journey file, not the test: `journeys/shop.journey` compares with
  `.snapshots/journeys/shop.verified.journey`. Moving the file orphans it.
- A relative path resolves against the test project (build metadata, else the nearest `*.csproj` above the caller). The
  `RunAsync(file, browser, options)` overload resolves against the current folder.
- A failed step does not throw. `transcript.Failure` is set and `Match` fails, naming the step first. A malformed
  journey throws `JourneyParseException`.
- `TranscriptMode.Off` still fails on a failing step or `expect` line. `TranscriptMode.Shape` throws
  `ArgumentException`: it is for `.http` runs.
- Chrome must be installed (`CDP_CHROME`, `CHROME_PATH`, usual folders, `PATH`). One Chrome per process; each journey
  gets its own context.
- Use `find role ... --name`, `find label` or CSS. `@e` refs and `eval` are not journey commands. `open about:blank` is
  refused.
- `RunAsync` never checks `@requires` or `@mutates`; call `SkipReason` first.

Docs: https://snapshots.nullean.net/formats/browser
