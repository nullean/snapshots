---
name: nullean-snapshots-browser-assertions
description: >-
  Use when writing tests that run browser journeys (`.journey` files) or snapshot a page's accessibility tree with
  AwesomeAssertions. Nullean.Snapshots.Browser.Assertions adds `JourneyTest.RunAsync`, `MatchJourneySnapshot` and
  `MatchAriaSnapshot`.
---

# Nullean.Snapshots.Browser.Assertions

`using Nullean.Snapshots.Browser;` gives one call that runs a journey in Chrome and asserts its transcript. Running
journeys outside a test is Nullean.Snapshots.Browser; the journey options are documented there.

## The calls you need

A test case per journey file (`[JourneyFiles]` is in Nullean.Snapshots.TUnit):

```csharp
[Test]
[JourneyFiles("journeys")]
public Task Run(SnapshotFile journey) =>
    JourneyTest.RunAsync(journey, new() { BaseUrl = new Uri("http://localhost:5000/") });
```

Skip a journey the site does not support, with the reason shown:

```csharp
if (JourneyTest.SkipReason(journey, options) is { } why)
    Skip.Test(why);
await JourneyTest.RunAsync(journey, options);
```

Run and assert in two steps, to pass `SnapshotOptions` or a reason:

```csharp
var transcript = await JourneySnapshot.RunAsync("journeys/shop.journey", options);
transcript.Should().MatchJourneySnapshot(because: "the basket shows the lamp");
```

One page's accessibility tree, from Nullean.Cdp, as `.verified.aria`:

```csharp
(await page.SnapshotAsync(new() { Compact = true })).Should().MatchAriaSnapshot();
```

## Gotchas

- TUnit 1.78's analyzer rejects `[JourneyFiles]` on a `SnapshotFile` parameter (error `TUnit0001`). The cases run fine:
  add `<NoWarn>$(NoWarn);TUnit0001</NoWarn>` to the test project.
- The journey's verified file is keyed by the journey path (`.snapshots/journeys/shop.verified.journey`), not the test.
  `MatchAriaSnapshot` is keyed by the test method; pass `name` for several in one test.
- `[JourneyFiles]` throws `InvalidOperationException` when the folder has no `*.journey` files. A class with a more
  specific folder (`journeys/admin`) takes those files away from `journeys`.
- `MatchJourneySnapshot` extends `ObjectAssertions`, so it compiles on anything; it fails unless the subject is a
  `JourneyTranscript`.
- `MatchAriaSnapshot` fails with "Expected an accessibility tree" when the string is not one.
- `JourneyTest.RunAsync` does not skip. Call `SkipReason` (or `Unmet` with a `JourneyContext`) first.
- A failed step fails the test even when the partial transcript matches; the message names the step first.
- `# @tags smoke` becomes a TUnit category: `--treenode-filter "/*/*/*/*[Category=smoke]"`.

Docs: https://snapshots.nullean.net/formats/browser
