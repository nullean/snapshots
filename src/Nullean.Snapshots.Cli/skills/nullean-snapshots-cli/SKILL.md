---
name: nullean-snapshots-cli
description: >-
  Use when a Nullean.Snapshots test failed and you need to see, accept or reject the received snapshot, or check a
  `.journey` or `.http` file without a test run. `snap` is the command line tool for that.
---

# Nullean.Snapshots.Cli

The `snap` dotnet tool works on snapshot files only. Do not use it to decide whether a change is intended: read the
diff.

## The calls you need

```sh
snap status --json                 # pending snapshots; exits 1 when there are any
snap diff CatalogTests/Renders     # full key, unique suffix, or glob
snap accept CatalogTests/Renders   # only after reading the diff
snap reject --all                  # delete received files, keep verified
snap orphans                       # after a full test run
snap journey run 'tests/Shop.Tests/journeys/**/*.journey' --base-url http://localhost:5000/
snap http run 'tests/Shop.Tests/http/**/*.http' --env dev --var host=http://localhost:5000
snap journey compare 'journeys/**/*.journey' --baseline https://prod.example.com/ --target https://preview.example.com/
snap journey compare ... --reuse-baseline --record 'journeys/known/{name}.differences'
snap http compare 'http/**/*.http' --baseline https://api.example.com --target https://api-preview.example.com
```

## Comparing two sites: `.differences`

`compare` runs a journey or `.http` file against a baseline and a target and fails only on differences no rule expects. Rules are in
`{file}.differences` next to the file (and `.differences` in any folder above it):

```text
include  //tests/shared.differences      # must exist; its rules are shared and never stale (before the first ##)
include? known/shop.differences          # may be missing: the differences of one preview, which --record fills

## snapshot -c -s nav                    # a section: the step's command, or the request as written; a glob
added   link "What's new"                # an accessibility node that appeared; removed / changed / ignore too
changed heading [level=1] "Docs" -> "Documentation"
added   text "GET {origin}/fonts/*.woff2 404"     # a line added to a read's text (removed text "..." for a line gone)
ignore  url                              # rules before any ## apply to every section
maybe added body $..debug                # may not match; .http targets: status, header NAME, body PATH, text
```

- A failure prints the rules that would explain it. `--record <file>` appends them to an `include?` file of the rules instead of
  failing; `{name}` in it is the file's name (one file of differences per journey). `--prune <file>` removes stale rules from it.
- A rule that explained nothing is stale and fails the run, except `maybe`, shared rules (`include`, folder files), and rules of an
  `include?` file whose heading this file's sections do not have.
- Values are `"quoted globs"` (`*`), `/regular expressions/` or one word. `--reuse-baseline` skips the baseline site if seeded.
- Docs: https://snapshots.nullean.net/workflow/expected-differences

## Gotchas

- Never accept automatically, never `accept --all` without reading every diff, and never in CI.
- A `(stale)` received file is older than the latest test run of its project: left over from an earlier run (after a mutation run and a
  revert it holds the mutated output). `accept --all` skips it; run the tests again, or `accept <key>` it, or `--include-stale`.
- `snap` with no command is `snap review`, which waits for key presses.
- `journey run` and `http run` write received files but never accept, even with `SNAPSHOT_ACCEPT=1`.
- `journey run` exits 0 all match, 1 a diff, a failed step or all skipped, 2 usage, 3 no Chrome.
- `orphans --delete` after a filtered run deletes snapshots that are still used.
- `--root` sets the search folder; the current folder by default.

Docs: https://snapshots.nullean.net/workflow/snap
