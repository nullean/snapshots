---
name: nullean-snapshots-tunit
description: >-
  Use when snapshot tests run on TUnit: data-driven or inherited tests that need one snapshot per case, or one test
  case per input file in a folder. Nullean.Snapshots.TUnit tells the store which TUnit test is running and adds the
  `[SnapshotFiles]` data source.
---

# Nullean.Snapshots.TUnit

The TUnit adapter for Nullean.Snapshots: snapshot names come from the running test's class, method and arguments.
Reference it from every TUnit test project that asserts snapshots; it adds no assertions of its own (those are in
`Nullean.Snapshots.AwesomeAssertions`).

## The calls you need

`using Nullean.Snapshots.TUnit;` for the attribute. Nothing else to call: a `buildTransitive` targets file compiles a
module initializer into the test project that runs
`TUnitTestIdentity.Register()`.

```csharp
[Test]
[Arguments("kibana")]
[Arguments("beats")]
public void Renders(string product) =>
    Render(product).Should().MatchSnapshot(); // Renders.<argsHash>.verified.txt per case
```

One case per file under a project folder, named after the file (`Formats(nested/case)`):

```csharp
[Test]
[SnapshotFiles("cases", "*.md")]
public void Formats(SnapshotFile file)
{
    var result = Snapshot.MatchFile(Format(file.Path), SnapshotFormats.Text, file.Path);
    if (!result.Passed) Assert.Fail(result.FailureMessage!);
}
```

Registering by hand, after `<SnapshotsTUnitAutoRegister>false</SnapshotsTUnitAutoRegister>`, in a class of its own:

```csharp
[Before(Assembly)]
public static void Setup() => TUnitTestIdentity.Register();
```

## Gotchas

- Without this package the class folder is the test file name and arguments add no hash, so every case of a
  data-driven test shares one verified file.
- The hash is of each argument's invariant text, `ToString()` for objects. A class without a `ToString()` override
  hashes the same for every case and throws `InvalidOperationException` ("pass a name"). Use records or primitives.
- A fixture from `[ClassDataSource<T>]` stays out of the hash when it is the only data source.
- The folder is the runtime class: `[InheritsTests]` classes get their own snapshots, nested classes `Outer.Inner`.
- TUnit's analyzer rejects `[SnapshotFiles]` with error `TUnit0001` (`TestDataRow<SnapshotFile>` against a
  `SnapshotFile` parameter). The test runs correctly; add `<NoWarn>$(NoWarn);TUnit0001</NoWarn>` to the project.
- `[SnapshotFiles]` with no matching file fails discovery with `InvalidOperationException`. It skips `bin`, `obj`,
  `node_modules` and dot folders.
- `SnapshotFile.Path` is relative to the test project, not the working directory. `Snapshot.MatchFile` keys the
  snapshot by that path: `.snapshots/cases/nested/case.verified.txt`.
- The failure message carries a `--treenode-filter` that reruns just that test.

Docs: https://snapshots.nullean.net/reference/layout
