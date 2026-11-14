---
name: nullean-snapshots-publicapi
description: >-
  Use when a .NET test should pin down a library's public API, so that an API change fails a test and shows in the
  pull request. Nullean.Snapshots.PublicApi stores the API as a .api file (C#, one declaration per line) and reports
  each change by path with the version bump it calls for and who it breaks.
---

# Nullean.Snapshots.PublicApi

## The call you need

`using Nullean.Snapshots.PublicApi;`

```csharp
typeof(Widget).Assembly.Should().MatchApiSnapshot();                        // one .verified.api
typeof(Widget).Assembly.Should().MatchApiSnapshot(ApiSplit.Namespace);      // one per namespace
typeof(Widget).Assembly.Should().MatchApiSnapshot(
	comparison: new ApiComparisonOptions { NotExtensionPoints = ["Lib.Internal.*"] });
```

`ApiSplit` and `ApiComparisonOptions` come from `Nullean.PublicApi` and `Nullean.PublicApi.Diff`.

## Reading a failure

Each line is one change: the path (`Lib.Drawing › Widget › Draw()`), what changed, `[major|minor|patch: breaks callers|implementers|inheritors, compiled code|source]`, and the line in the verified file. The summary says the version change the whole set calls for. When the change is intended, accept the received file the way any snapshot is accepted, and bump the version the summary names.

## Gotchas

- The assembly needs a file: `MatchApiSnapshot` fails for a single-file app, whose assemblies have no location.
- `public` is left out of the file: everything in it is visible from outside, and only `protected` members say so.
- A change that only breaks implementers of a type nobody outside can implement (sealed, no public or protected constructor) is not a break at all.
