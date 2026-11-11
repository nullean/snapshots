---
name: nullean-snapshots-browser-console
description: >-
  Use when a console tool or CI script needs to run a folder of browser journeys (`.journey` files) and print the
  results. Nullean.Snapshots.Browser.Console runs them in one shared Chrome with the output and exit codes of
  `snap journey run`.
---

# Nullean.Snapshots.Browser.Console

`using Nullean.Snapshots.Browser.Console;` runs a suite of journeys, streams each result, groups skips and repeats the
failures at the end. For one journey in a test, use Nullean.Snapshots.Browser.Assertions; to just run a suite, `snap
journey run` already does this.

## The calls you need

Run every journey under a folder against a site:

```csharp
var options = new JourneyRunOptions
{
    Files = JourneyRunFile.FromPaths(root, Directory.EnumerateFiles(root, "*.journey", SearchOption.AllDirectories)),
    BaseUrl = new Uri("https://shop.test/"),
    ReadOnly = true,
    Rerun = "my-tool run --only {id}",
};
return await JourneyConsoleRunner.RunAsync(options, Console.Out, Console.Error);
```

Compare transcripts with their verified files too (left out, only steps and `expect` lines count):

```csharp
var compared = new JourneyRunOptions
{
    Files = files,
    BaseUrl = site,
    Compare = (file, transcript) => JourneySnapshot.Match(transcript),
};
```

Select by flags and tags:

```csharp
var tagged = new JourneyRunOptions { Files = files, Features = new HashSet<string> { "preview" }, Tags = new HashSet<string> { "smoke" } };
```

## Gotchas

- The result is a `JourneyExitCode`: `Ok` 0, `Failed` 1, `Usage` 2 (a journey does not parse), `Browser` 3 (Chrome could
  not start).
- A run where every journey was skipped exits 1. Set `AllowAllSkipped` when that is fine.
- `ReadOnly` skips `@mutates` journeys unless `AllowMutations`. An unmet `@requires` or a missing tag also skips.
- Under `GITHUB_ACTIONS=true` it writes error annotations and appends to `GITHUB_STEP_SUMMARY`. Turn off with
  `GitHubActions = false`, `StepSummary = false`.
- Output is plain (no colour or status line) when `CI` is set or stdout is redirected.
- A journey's id is its path relative to `root` without `.journey`; `{id}` in `Rerun` is replaced by it.
- There is no secrets or timeout option: `$env:NAME` reads the process environment. `Parallel` defaults to 4.
- `IJourneyReporter` is public, but `RunAsync` always uses `ConsoleJourneyReporter`.

Docs: https://snapshots.nullean.net/formats/browser#run-a-suite-from-the-console
