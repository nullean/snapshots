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

## A live site and variants

The text a page returns drifts on a live site, so a transcript compared with a verified file fails for reasons that are
not bugs. Turn the comparison off and keep the assertions:

```csharp
JourneyTest.RunAsync(journey, new() { BaseUrl = site, Transcript = TranscriptMode.Off });
```

With `TranscriptMode.Off` the journey still runs every step, so a step that fails, an `expect` line included, fails the
test with the usual message naming the step. Nothing is compared and no verified file is needed or written.

Output that differs for a reason you want to keep, such as the navigation a feature flag adds, gets a verified file of
its own. `Variant = "preview"` compares `journeys/shop.journey` with `.snapshots/journeys/shop.preview.verified.journey`
and leaves `shop.verified.journey` alone. A variant is letters, digits, `.`, `-` and `_`. `snap orphans` counts a
variant file as used when a run compared with it.

## Journeys that apply to some sites

A journey can say which sites it is for with `# @requires` in its header (Nullean.Cdp's
[journey header](https://cdp.nullean.net/journeys/)), and a `feature … end` block holds steps for one flag state. Tell the
run what the site is, and ask whether a journey applies before running it:

```csharp
var site = new JourneySnapshotOptions
{
    BaseUrl = target.Url,
    Features = target.Features,      // the enabled flags, kebab-case
    BuildType = target.BuildType,    // "isolated", "assembler", "codex"
    Env = target.Env,                // "edge", "prod"; null when unknown
    ReadOnly = target.IsLive,        // refuse every request that is not GET, HEAD or OPTIONS
};

[Test]
[JourneyFiles("journeys")]
public Task Run(SnapshotFile journey)
{
    if (JourneyTest.SkipReason(journey, site) is { } why)
        Skip.Test(why);              // TUnit; any framework's skip works
    return JourneyTest.RunAsync(journey, site);
}
```

`JourneyTest.Unmet(journey, context)` returns the first `@requires` condition the context does not meet, as written
(`@requires navigation-preview`), or `null`. `SkipReason` does the same from the run's options and also skips a journey
marked `@mutates` on a read-only run. A skipped test shows its reason, so a journey that does not run is never a
silent pass. `JourneySnapshot.Metadata(file)` returns the whole header: tags, requirements, monitor, alerts.

A `feature` block the run does not meet is not an error. The transcript shows it once, as a section headed `feature …`
with `skipped: <the condition>`, so a verified transcript records which flag state it was taken in. Compare each flag
state with its own [variant](#a-live-site-and-variants).

`ReadOnly` is enforced in the browser, not by the journey: a write to an allowed host fails the step that caused it, and
`WritableUrls` lists the globs, such as a telemetry endpoint, that may still be written to.

### Select journeys by tag

`# @tags navigation smoke` in a journey's header (and in a `.http` file's) becomes a TUnit category on each case that
`[JourneyFiles]` or `[HttpFiles]` yields. A filter then runs only the tagged ones:

```sh
dotnet test --treenode-filter "/*/*/*/*[Category=smoke]"
dotnet test --treenode-filter "/*/*/NavigationJourneys/*[Category=monitor]"
```

The tags are read when TUnit discovers the tests, from the comment lines before the first step. Whether a journey
applies to a run cannot be decided then, because a site's flags are known only once it is up. That is the
[skip](#journeys-that-apply-to-some-sites) at run time, so a filtered-in journey can still show as skipped.
`SnapshotFile.Tags(assembly)` returns the same names.

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
| `Transcript` | `--transcript compare\|off` | `Off` runs the steps and `expect` lines and compares no transcript; see [a live site](#a-live-site-and-variants) |
| `Variant` | `--variant <name>` | compares with `{journey}.{variant}.verified.journey`; see [a live site](#a-live-site-and-variants) |
| `Features`, `BuildType`, `Env` | `--feature`, `--build-type`, `--env` | what `feature` blocks and `@requires` are judged against; see [journeys that apply](#journeys-that-apply-to-some-sites) |
| `ReadOnly`, `WritableUrls` | `--read-only`, `--writable` | refuse writes, except to these URL globs |
| `Viewport` | `--viewport` | the size every journey starts with; Chrome's default is a phone layout on a responsive site |
| `UserAgent`, `Headers` | `--user-agent`, `--header` | what every page of the run sends, such as the identity a site uses to tell a monitor from a visitor |
| `ScreenshotOnFailure` | `--screenshot-on-failure` | take a screenshot of the viewport when a step fails (`JourneyFailure.Screenshot`), written to `failure.png` in the artifacts folder |
| `OnStep` | | called before each step, for a progress display |

## Run a suite from the console

`Nullean.Snapshots.Browser.Console` runs a set of journey files in one shared Chrome and shows the run the way
`snap journey run` does. A journey that does not apply (`@requires`, `@tags`, `@mutates` on a read-only run) is skipped with its
reason, and a reason that many journeys share is shown once with a count. Each result is printed as it finishes, and the
failures are repeated at the end with the totals. On a terminal a status line shows what is running, a failure shows the journey's
lines around the failing one, and icons and colour are on; in CI, in a pipe, or with `NO_COLOR` the output is plain lines.

```csharp
var options = new JourneyRunOptions
{
    Files = JourneyRunFile.FromPaths(root, Directory.EnumerateFiles(root, "*.journey", SearchOption.AllDirectories)),
    BaseUrl = new Uri("https://shop.test/"),
    Features = new HashSet<string> { "preview" },
    ReadOnly = true,
    Rerun = "my-tool run --only {id}",
};
return await JourneyConsoleRunner.RunAsync(options, Console.Out, Console.Error);
```

`Compare` decides whether transcripts are compared: leave it out to check only that every step and `expect` line passes, or
pass a function that compares the transcript with the verified one (`snap` does). The exit code is a `JourneyExitCode`.
`ConsoleJourneyReporter.ContextBlock` prints the facts a run starts from, one per line, for a tool that wants a header.

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
