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

It writes received files for `snap review` and prints the usual failure message, but never accepts (not even with
`SNAPSHOT_ACCEPT=1`) and never writes the run manifest, so it cannot make other snapshots look orphaned. Exit codes:
0 all match, 1 a transcript differs or a step failed, 2 usage or a journey error, 3 Chrome could not start.

## HTTP files

`snap http run` runs [`.http` files](../formats/http.md) and compares each transcript with its verified file, as a
test would, against a server that is already running:

```sh
snap http run 'tests/Shop.Tests/http/**/*.http' --env dev --var host=http://localhost:5000
```

`--env` selects the environment from `http-client.env.json`, `--var name=value` (repeatable) beats file variables and
the environment, and `--header` (repeatable) writes another response header, as a test's `Headers` option does.
`--variant <name>` compares with `{file}.{variant}.verified.responses`, and `--transcript off` sends the requests
without comparing the transcript. It
sends with the runner's own client, which follows redirects itself and decompresses bodies; secrets come from the
environment files, `.env` files, the process environment and user secrets, and are redacted as in a test.

Like `snap journey run` it writes received files for `snap review`, never accepts and never writes the run manifest.
Globs skip `bin`, `obj`, `node_modules` and dot folders. Exit codes: 0 all match, 1 a transcript differs or a request
failed (a connection refused, a timeout, an unresolved variable), 2 usage or a parse error in a `.http` file, 3 a
`.http` file, environment file or user secrets file could not be read. An error status such as 404 is a response, not
a failure.

These two are the only commands that do more than read and write files.
