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
```

## Gotchas

- Never accept automatically, never `accept --all` without reading every diff, and never in CI.
- `snap` with no command is `snap review`, which waits for key presses.
- `journey run` and `http run` write received files but never accept, even with `SNAPSHOT_ACCEPT=1`.
- `journey run` exits 0 all match, 1 a diff, a failed step or all skipped, 2 usage, 3 no Chrome.
- `orphans --delete` after a filtered run deletes snapshots that are still used.
- `--root` sets the search folder; the current folder by default.

Docs: https://snapshots.nullean.net/workflow/snap
