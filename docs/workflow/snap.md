# The snap CLI

`snap` works on files only. It finds received files without evaluating MSBuild: it looks in
`artifacts/.snapshots/received/` and `.artifacts/.snapshots/received/` under the current folder (or `--root`), and in
`obj/snapshots/received/` next to each project file.

| Command | Does | Exits |
|---|---|---|
| `snap` or `snap review` | each pending snapshot with its diff; accept, reject, skip or quit | 1 while any are left |
| `snap status [--json]` | lists pending snapshots | 1 when there are any |
| `snap diff <key>` | the line diff of one snapshot | 0 |
| `snap accept <key…> \| --all` | writes them as verified, deletes the received files; an [inline snapshot](../formats/inline.md) is written into its test's source | 0, or 1 when an inline one cannot be |
| `snap reject <key…> \| --all` | deletes the received files | 0 |
| `snap orphans [--json] [--delete]` | verified files the last test run did not use | 1 when there are any |

Usage errors exit 2 and IO errors 3.

## Keys

A key is `{Project}/{folder}/{Class}/{Method}[.{name}][.{hash}]`, for instance
`Shop.Tests/Rendering/CatalogTests/Renders`. Commands take the full key, any unique suffix that starts at a `/`
(`Renders`, `CatalogTests/Renders`), or a glob: `*` within one segment, `**` across them (`CatalogTests/*`,
`**/Renders*`). An ambiguous suffix lists the candidates and changes nothing.

An [inline snapshot](../formats/inline.md)'s key ends in the line of its call (`Renders.L12`), and `snap accept` edits the
string literal in the test's `.cs` file, so read `git diff` of the test files as well as the verified ones.

## Review

```text
$ snap

[1/2] changed  Shop.Tests/Rendering/CatalogTests/Renders  .html
--- tests/Shop.Tests/.snapshots/Rendering/CatalogTests/Renders.verified.html
+++ .artifacts/.snapshots/received/Shop.Tests/Rendering/CatalogTests/Renders.received.html
@@ line 4 @@
 		<tr>
-			<td>kibana</td>
+			<td>Kibana</td>
[a]ccept, [r]eject, [s]kip, [q]uit?
```

On a terminal it reads single keys and colours the diff (not with `NO_COLOR` set). With its input redirected it reads
one answer per line, so `printf 'a\nr\n' | snap review` works in scripts.

## Orphans

Each test run writes a manifest of the verified files it used, in the project's received folder. `snap orphans` lists
verified files the latest run did not use: snapshots of deleted or renamed tests. Run the whole suite first; a
filtered run leaves out what it skipped. `--delete` removes them.

## Journeys

`snap journey run` runs [browser journeys](../formats/browser.md) in Chrome and compares each transcript with its
verified file, as a test would, so a journey an agent just wrote can be checked without a test run:

```sh
snap journey run 'tests/Shop.Tests/journeys/**/*.journey' --base-url http://localhost:5000/
```

`--allow` adds hosts the journeys may reach, `--file-root` sets the folder `file://` URLs may use, and `--artifacts` moves
screenshots and HAR files out of each journey's folder next to its received transcript. `--variant <name>` compares
with `{journey}.{variant}.verified.journey`, and `--transcript off` runs the steps and `expect` lines without comparing
the transcript, so no verified file is needed.

A run reads the site the way a test does. `--feature <name>` (repeatable), `--env` and `--build-type` say what `feature`
blocks and `# @requires` lines are judged against, and a journey that does not apply is skipped with its reason.
`--tag <name>` (repeatable) runs only journeys with that `# @tags`. `--read-only` refuses every request that is not
GET, HEAD or OPTIONS, skips journeys marked `# @mutates` (`--allow-mutations` runs them), and `--writable <glob>`
(repeatable) lets a URL be written anyway. `--viewport 1280x800`, `--user-agent` and `--header "Name: value"`
(repeatable) set what every page of the run sends. `--parallel <n>` (default 4) sets how many journeys run at once in one
Chrome, `--junit <file>` writes a JUnit report, and `--allow-all-skipped` exits with 0 when every journey was skipped.

`--screenshot-on-failure` takes a screenshot of the page when a step fails. It is written to `failure.png` in the journey's
artifacts folder, named in the failure, and on a terminal that can show images (Kitty, Ghostty, iTerm2, WezTerm, Sixel, with
half blocks as the fallback) drawn under the error. It is off unless asked for, because a page can show private data;
`NULLEAN_TERMINAL_IMAGES=none` keeps the image out of the terminal.

It prints each journey as it finishes, collapses a skip reason that many journeys share (`--verbose` lists them), and
repeats the failures at the end with the totals. On a terminal that adds a status line, icons, colour, and the journey's
lines around a failing one; in CI, in a pipe, or with `NO_COLOR` the output is plain lines.

It writes received files for `snap review` and prints the usual failure message, but never accepts (not even with
`SNAPSHOT_ACCEPT=1`) and never writes the run manifest, so it cannot make other snapshots look orphaned. Exit codes:
0 all match, 1 a transcript differs, a step failed, or every journey was skipped, 2 usage or a journey error, 3 Chrome could
not start. A tool that wants the same output calls
[`JourneyConsoleRunner`](../formats/browser.md#run-a-suite-from-the-console).

## HTTP files

`snap http run` runs [`.http` files](../formats/http.md) and compares each transcript with its verified file, as a
test would, against a server that is already running:

```sh
snap http run 'tests/Shop.Tests/http/**/*.http' --env dev --var host=http://localhost:5000
```

`--env` selects the environment from `http-client.env.json`, `--var name=value` (repeatable) beats file variables and
the environment, and `--header` (repeatable) writes another response header, as a test's `Headers` option does.
`--variant <name>` compares with `{file}.{variant}.verified.responses`, `--transcript off` sends the requests
without comparing the transcript, `--transcript shape` compares only the properties of JSON bodies
([shapes](../formats/http.md#shapes-properties-without-values)), and `--read-only` refuses any request that is not GET, HEAD or OPTIONS unless it carries
`# @readonly`. It
sends with the runner's own client, which follows redirects itself and decompresses bodies; secrets come from the
environment files, `.env` files, the process environment and user secrets, and are redacted as in a test.

Like `snap journey run` it writes received files for `snap review`, never accepts and never writes the run manifest.
Globs skip `bin`, `obj`, `node_modules` and dot folders. Exit codes: 0 all match, 1 a transcript differs or a request
failed (a connection refused, a timeout, an unresolved variable), 2 usage or a parse error in a `.http` file, 3 a
`.http` file, environment file or user secrets file could not be read. An error status such as 404 is a response, not
a failure.

These two are the only commands that do more than read and write files.

## Comparing two sites

```sh
snap journey compare 'tests/Shop.Tests/journeys/**/*.journey' \
  --baseline https://docs.example.com/ --target https://docs-preview.example.com/
```

runs each journey against the baseline site, seeds its transcript into the received tree (`{journey}.baseline.journey`,
never checked in and never a snapshot), then runs it against the target and compares the two. What may differ is written
in `{journey}.differences` next to the journey: see [expected differences](expected-differences.md). A difference no rule
accounts for fails the run and the message says which rule would explain it; a rule that matched nothing fails too.

`--reuse-baseline` runs the baseline site only for journeys that have no seeded baseline, so iterating on the target does
not send to production again. `--record <file>` appends a rule for every unexpected difference to a file the journey's
rules `include?`, instead of failing, and `--prune <file>` removes the stale rules of such a file: how an agent works
through a preview and how a change that shipped stops being excused. The runs are the console runner's, so `@requires`,
`@mutates`, `--read-only`, `--parallel`, `--junit` and the other options of `snap journey run` work the same, and exit
codes are those of `run`, with a rules file that does not parse as 2.

**Screenshots.** A journey's `screenshot nav.png` steps write PNGs, and a comparison gives each site a folder of its own
(`{journey}.baseline.artifacts` and `{journey}.target.artifacts` in the received tree), so the second run does not empty the
first's. Pictures with the same name are compared pixel by pixel, as [image snapshots](../formats/images.md) are, and each
pair that differs gets a diff image next to the target's: the page faded, the changed pixels in red, a blue box around each place. When
the changes are a small part of the picture, a zoom (`{name}.zoom.png`) crops the diff to them with 32 pixels around, at its own size and not
enlarged, so what is in view is the change and what surrounds it; that is what is drawn in the terminal, enlarged smoothly to fill about 60 by 12 cells so a few words are readable
(the file itself is not enlarged). They are listed after the
run with where the pixels differ, and drawn there on a terminal that can show images.

```
1 screenshot(s) differ between the baseline and the target (not a failure: --image-diff fail makes it one):
  journeys/shop  nav.png: 296 of 360,000 pixels differ (0.08%), 0 allowed; 159 anti-aliased pixel(s) left out
      x 99-171, y 11-23 (73x13): 296 pixel(s)
      diff: .artifacts/.snapshots/received/Shop.Tests/journeys/shop.target.artifacts/nav.diff.png
      zoom: .artifacts/.snapshots/received/Shop.Tests/journeys/shop.target.artifacts/nav.zoom.png
```

`--image-diff report` (the default) lists them, `fail` makes a differing screenshot a failure, and `off` compares none. A page
that differs in what it says differs in pixels, so on a live site `report` is the useful setting and `fail` is for pages that
should look the same. `--screenshot-on-failure` takes a screenshot when a step fails on either site. With `--artifacts <folder>`
each site writes to a folder of that name inside it, and pictures are not compared.

`snap http compare` does the same for `.http` files:

```sh
snap http compare 'tests/Shop.Tests/http/**/*.http' --baseline https://api.example.com --target https://api-preview.example.com
```

Each site's URL is `{{host}}` for its run (`--host-variable` names another), `--baseline-env` and `--target-env` pick an
environment of `http-client.env.json` per site, and each site's origin is written as `{origin}` wherever it appears in a
transcript. Both runs are written as [shapes](../formats/http.md#shapes-properties-without-values), so only properties
added, dropped or retyped differ; `--values` compares values as well.
