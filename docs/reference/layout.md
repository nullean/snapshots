# Where files go

Verified files live in one folder per test project, mirroring the test file's folder, with a folder per test class:

```text
tests/Shop.Tests/
├── Rendering/CatalogTests.cs
└── .snapshots/
    └── Rendering/
        └── CatalogTests/
            ├── Renders.verified.html
            ├── Renders.header.verified.html         {Method}.{name}
            └── RendersPage.a1b2c3d4.verified.html   {Method}.{arguments hash}
```

- **One folder per project**, not one next to each test file: source folders hold only source, and a pull request's
  snapshot changes sit under one prefix.
- **By the test file's folder**, not its namespace. Moving a test file leaves its snapshots behind;
  `snap orphans` finds them.
- **A folder per runtime class**, so `[InheritsTests]` classes that share a base class's methods get their own files.
- **The arguments hash** (eight hex digits) tells data-driven cases apart. It is stable across machines, cultures and
  versions. A fixture injected with TUnit's `[ClassDataSource<T>]`, through the constructor or a method parameter, is
  not test data and stays out of the hash, so a class with a fixture gets plain `{Method}` names.

Received files never go into the source tree:

```text
.artifacts/.snapshots/received/{Project}/Rendering/CatalogTests/Renders.received.html   with an artifacts path
tests/Shop.Tests/obj/snapshots/received/Rendering/CatalogTests/Renders.received.html     otherwise
```

Both are ignored in practically every .NET repository already, so a received file cannot be committed by accident.

## MSBuild properties

| Property | Default |
|---|---|
| `SnapshotsDirectory` | `$(MSBuildProjectDirectory)/.snapshots` |
| `SnapshotsReceivedDirectory` | `$(ArtifactsPath)/.snapshots/received/$(MSBuildProjectName)`, or `obj/snapshots/received` |
| `SnapshotsIncludeInProject` | `true`: the verified files show in the IDE |
| `SnapshotsTUnitAutoRegister` | `true`: the TUnit adapter registers itself |

The library writes only inside these two folders: every write goes through a file system scoped to them.
