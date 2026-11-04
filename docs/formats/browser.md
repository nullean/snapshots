# Browser journeys

```csharp
using Nullean.Snapshots.Browser;

public static IEnumerable<string> Journeys() => JourneySnapshot.Find();   // journeys/**/*.journey in the project

[Test, MethodDataSource(nameof(Journeys))]
public async Task Runs(string journey) =>
    (await JourneySnapshot.RunAsync(journey, new() { BaseUrl = new("http://localhost:5000/") }))
        .Should().MatchJourneySnapshot();
```

A **journey** is a text file of [agent-browser](https://github.com/vercel-labs/agent-browser) commands, one per
line, run in Chrome by [Nullean.Cdp](https://github.com/nullean/cdp) without Node or a daemon:

```text
# journeys/shop.journey
open index.html
find label Search fill lamp
find role button click --name Search --exact
snapshot -c -s "#results"
find role button click --name "Add red lamp"
get text #count
```

An agent that explored a site with agent-browser can write down the steps that matter as they were, and the same file
replays in agent-browser after `Journey.ToAgentBrowserBatch`. The commands, quoting and what is left out (refs,
screenshots, `eval`, cookies) are documented with Nullean.Cdp.

## The transcript

The snapshot belongs to the **journey file**, not the test: `journeys/shop.journey` is compared with
`.snapshots/journeys/shop.verified.journey`, so a test and [`snap journey run`](../workflow/snap.md) share it. It holds
one section per read (`snapshot`, `get`, `is`, `find … text`):

```text
## snapshot -c -s #results
url: {origin}/index.html
- list
  - listitem [level=1]
    - StaticText "Red lamp "
    - button "Add red lamp"

## get text #count
url: {origin}/index.html
> 1
```

- A section is headed by the step's command without its line number, so inserting a step shifts nothing.
- Trees are compared node by node, and an inserted section or list item is one difference:

  ```text
  Journey did not match snapshot: tests/Shop.Tests/.snapshots/journeys/shop.verified.journey
  1 difference(s):
  $[0].tree[0].children[0].name   expected "Shop"   actual "Store"   (verified.journey:4:13)
  ```

  followed by a line diff.
- Text a read returns is quoted with `> ` on every line, so text from a page can never start a section or pass for a
  tree.
- The base URL's root is scrubbed to `{origin}`: the origin of an http(s) URL, or the whole folder of a `file:` URL.
- A step that fails stops the journey. The assertion fails, naming the step first, and the received transcript holds
  the reads before it:

  ```text
  Journey stopped at journeys/shop.journey:5: find role button click --name "Add red lamp"
  Timed out after 5s waiting for role button --name "Add red lamp" to be visible and enabled: no element matches.
  ```

## One page

`MatchAriaSnapshot` snapshots one accessibility tree as Nullean.Cdp prints it (`.verified.aria`), compared the same
way:

```csharp
(await page.SnapshotAsync(new() { Compact = true })).Should().MatchAriaSnapshot();
```

## Chrome

Chrome is found from `CDP_CHROME`, `CHROME_PATH`, the usual install folders, then the `PATH`; GitHub's runner images
have it. One Chrome is started per test process and each journey gets its own context, so journeys run in parallel
without sharing cookies or storage.
