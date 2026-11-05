# Browser journeys

```csharp
using Nullean.Snapshots.Browser;
using Nullean.Snapshots.TUnit;

public class ShopJourneys(ShopServer server)   // your usual setup: fixtures, hooks
{
    [Test]
    [JourneyFiles("journeys")]                 // a test case per journeys/**/*.journey
    public Task Run(SnapshotFile journey) =>
        JourneyTest.RunAsync(journey, new() { BaseUrl = server.Url });
}
```

Each journey is its own test case, named after its file: `Run(checkout)`, `Run(basket/empty)`. Run or filter one
on its own like any test.

A **journey** is a text file of [agent-browser](https://github.com/vercel-labs/agent-browser) commands, one per
line, run in Chrome by [Nullean.Cdp](https://cdp.nullean.net) without Node or a daemon:

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
`eval`, cookies, tabs) are in Nullean.Cdp's [command reference](https://cdp.nullean.net/journeys/commands/).
`screenshot` and `network har stop` write into the journey's [artifacts folder](#screenshots-and-har-files).

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

## Setup per folder

Setup lives on the test class as usual. A folder whose journeys need something else, such as a signed-in user, gets
its own class with a more specific folder, and the outer class leaves that folder out:

```csharp
public class AdminJourneys(ShopServer server, AdminSession admin)
{
    [Test]
    [JourneyFiles("journeys/admin")]            // ShopJourneys no longer runs these
    public Task Run(SnapshotFile journey) =>
        JourneyTest.RunAsync(journey, new() { BaseUrl = server.Url, Secrets = admin.Secrets });
}
```

`[SnapshotFiles(folder, pattern)]` is the same data source for any kind of file, and `SnapshotFile.Find` lists the
files without a test framework.

## Checks

Nullean.Cdp's checks are reads, so they land in the transcript like any other, and their output is stable enough to
snapshot:

```text
# journeys/checks.journey
open index.html
a11y
links --all
network requests --failed
vitals --budget lcp=2500 cls=0.1
```

```text
## a11y
url: {origin}/index.html
> violations: 0

## links --all
url: {origin}/index.html
> links: 2 checked, 0 broken
> ok /index.html
> ok /basket.html
```

A new accessibility problem, a broken link or a failed request is then a difference in the transcript. Raw `timings`,
`vitals` and `metrics` change on every run, so snapshot them with `--budget`, which prints only whether each reading is
within its limit. Add `--strict` to `a11y`, `links` or `network requests` to stop the journey at the step instead.

## Assertions

A journey can assert as well as read. An `expect` line checks again and again until it holds or times out (5 s, or
`--timeout <ms>`), then fails the step:

```text
# journeys/shop.journey
open index.html
expect title "Fixture shop"
find role button click --name "Add red lamp"
expect text #count 1
get text #count
```

A passing assertion records nothing, so it changes no verified transcript: `shop.verified.journey` above holds the
`get text` and nothing for the `expect` lines. A failing one stops the journey like any failing step, and the received
transcript keeps the reads before it, so the failure and the diff of what was read come together. Had line 5 expected `2`:

```text
Journey stopped at tests/Shop.Tests/journeys/shop.journey:5: expect text #count 2
Timed out after 5s waiting for css #count to have text "2": received "1".
```

The matchers (visible, enabled, text, value, attribute, class, count, title, URL, negated with `--not`) are in Nullean.Cdp's
[command reference](https://cdp.nullean.net/journeys/commands/#expect). `snap journey run`, `[JourneyFiles]` test cases
and `JourneyTest.RunAsync` need nothing extra: they run the same lines.

## Screenshots and HAR files

`screenshot` and `network har stop` write files. In a test they go next to the journey's received transcript,
in a folder emptied before each run:

```text
.artifacts/.snapshots/received/Shop.Tests/journeys/checkout.artifacts/shots/home.png
```

`Snapshot.ArtifactsDirectory("journeys/checkout.journey")` returns that folder. Set
`JourneySnapshotOptions.ArtifactsDirectory`, or `snap journey run --artifacts`, to write somewhere else. Screenshots
are files, not part of the transcript: pixels differ between machines.

## Options

| `JourneySnapshotOptions` | `snap journey run` | |
|---|---|---|
| `BaseUrl` | `--base-url` | what relative `open` URLs resolve against; its root is scrubbed to `{origin}` |
| `AllowedHosts` | `--allow` | hosts the journey may reach beyond the base URL's and its `allow` lines |
| `FileRoot` | `--file-root` | the folder `file://` URLs may point into |
| `ArtifactsDirectory` | `--artifacts` | where screenshots and HAR files go |
| `Secrets` | | resolves `$env:NAME` references; the environment by default |
| `ActionTimeout`, `NavigationTimeout` | | Nullean.Cdp's 5 s and 30 s by default |

## Chrome

Chrome is found from `CDP_CHROME`, `CHROME_PATH`, the usual install folders, then the `PATH`; GitHub's runner images
have it. One Chrome is started per test process and each journey gets its own context, so journeys run in parallel
without sharing cookies or storage.

## Playwright and Elastic Synthetics

The journey files are plain text, so the same file can also run on [Playwright](https://playwright.dev) or be pushed to
[Elastic Synthetics](https://www.elastic.co/docs/solutions/observability/synthetics) as a browser monitor, through the
`@nullean/journeys` npm package: [Playwright and Elastic Synthetics](https://cdp.nullean.net/journeys/playwright/) in
Nullean.Cdp's documentation. Snapshots stay with the .NET run: it is `cdp run`'s reads that are compared with the verified
transcript, and the Playwright runs show that the same journey still passes elsewhere. Playwright's accessibility tree is
not the tree Nullean.Cdp prints, so a `snapshot` read on Playwright is never compared with a `.verified.journey`.
