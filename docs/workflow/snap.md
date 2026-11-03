# The snap CLI

`snap` works on files only. It finds received files without evaluating MSBuild: it looks in
`artifacts/.snapshots/received/` and `.artifacts/.snapshots/received/` under the current folder (or `--root`), and in
`obj/snapshots/received/` next to each project file.

| Command | Does | Exits |
|---|---|---|
| `snap` or `snap review` | each pending snapshot with its diff; accept, reject, skip or quit | 1 while any are left |
| `snap status [--json]` | lists pending snapshots | 1 when there are any |
| `snap diff <key>` | the line diff of one snapshot | 0 |
| `snap accept <key…> \| --all` | writes them as verified, deletes the received files | 0 |
| `snap reject <key…> \| --all` | deletes the received files | 0 |
| `snap orphans [--json] [--delete]` | verified files the last test run did not use | 1 when there are any |

Usage errors exit 2 and IO errors 3.

## Keys

A key is `{Project}/{folder}/{Class}/{Method}[.{name}][.{hash}]`, for instance
`Shop.Tests/Rendering/CatalogTests/Renders`. Commands take the full key, any unique suffix that starts at a `/`
(`Renders`, `CatalogTests/Renders`), or a glob: `*` within one segment, `**` across them (`CatalogTests/*`,
`**/Renders*`). An ambiguous suffix lists the candidates and changes nothing.

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
