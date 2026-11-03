# Failure messages

Every failure follows one template:

```text
{Format} did not match snapshot: {verified file}
{N} difference(s):
{one block per difference}
(+{M} more, see received)
Received: {received file}
Accept:   snap accept {key}   (or: dnx Nullean.Snapshots.Cli accept {key})
   or:    SNAPSHOT_ACCEPT=1 dotnet test --project {project} --treenode-filter "{filter}"
```

- **New snapshots** start `{Format} snapshot is new: no verified file at {verified file}` and list no differences.
- **Paths** are relative to the repository root (the nearest folder holding `.git`), with `/` on every OS.
- **The `--treenode-filter` line** appears with the TUnit adapter, which knows the running test; run as printed, it
  selects exactly that test.
- **`because`** goes into the first line: `JSON did not match snapshot because the catalog is stable: …`.

## Caps

At most **20 differences** and **80 lines** are shown. A difference that does not fit in the remaining lines is left
out whole rather than cut halfway, except a first difference longer than the cap on its own, which is cut so the
message still shows something. Everything left out is counted in `(+M more, see received)`; the received file always
has everything.

## Plain text

No colour codes, ever, and content is never used as a format string, so braces in snapshots are safe. The messages
read the same in a terminal, a CI log and an agent's context, and this repository keeps its own messages as golden
snapshots.
