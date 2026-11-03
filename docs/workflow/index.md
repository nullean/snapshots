# Workflow

1. A test fails: its message shows what differs and where the received file is.
2. You look: `snap diff <key>` or `snap review`.
3. You decide: `snap accept` writes the verified file, `snap reject` throws the received one away.
4. You commit the verified file, and read its diff in review like any other change.

Nothing is ever accepted for you. `SNAPSHOT_ACCEPT=1` accepts while tests run, on your machine; in CI it is ignored,
and the failure says so.

- [The snap CLI](snap.md)
- [Snapshots in CI](ci.md)
- [Working with coding agents](agents.md)
