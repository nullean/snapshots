# Decisions

Non-obvious choices, newest last. Each says what was chosen, and why when the why is not obvious.

## Fixed at the start

- Name `Nullean.Snapshots`, CLI command `snap`, MIT. .NET 10.
- Built on AwesomeAssertions; Verify is not used (v33+ adds a build-time sponsorship check).
- Never auto-accept. Accept with `snap accept` or `SNAPSHOT_ACCEPT=1`; with `CI=true` nothing is ever accepted.
- We own the JSON/YAML tree diff. `SystemTextJson.JsonDiffPatch` is a test-only oracle, never a runtime dependency.
- The CLI is a native AOT dotnet tool and works on files only; it references no format package.
- Libraries are AOT/trim-clean, except where a dependency cannot be (AngleSharp, to be confirmed).

## Where snapshots live

Verified snapshots sit in one folder per test project, mirroring the test file's folder:

```
{ProjectDir}/.snapshots/{test file dir relative to the project}/{RuntimeClass}/{Method}[.{name}][.{argsHash}].verified.{ext}
```

- **One folder per project, not one next to each test file.** Source folders hold only source, a pull request's
  snapshot changes sit under one prefix, and orphan detection compares one tree.
- **Mirrored by the test file's folder, not its namespace.** The folder is always known from `[CallerFilePath]` and
  the project directory, with or without the TUnit adapter, and it matches what people see. Moving a test file
  orphans its snapshots; `snap orphans` reports that.
- **A folder per runtime class.** `[InheritsTests]` reuses method names across derived classes, and the call site's
  `[CallerFilePath]` is the base class's file, so the runtime class name is what keeps them apart.
- **`.snapshots`, a dot folder.** Configurable through the `SnapshotsDirectory` MSBuild property.

Received snapshots never go into the source tree:

```
$(ArtifactsPath)/snapshots/received/{Project}/{same relative path}.received.{ext}     when ArtifactsPath is set
{ProjectDir}/obj/snapshots/received/{same relative path}.received.{ext}              otherwise
```

- Both are already ignored in essentially every .NET repository, so adopting the library needs no `.gitignore` change,
  a received file cannot be committed by accident, and CI that uploads `artifacts/` gets received files for free.
- Verified and received paths map to each other by swapping the prefix.

**Key.** `{Project}/{relative dir}/{Class}/{Method}[.{name}][.{argsHash}]`, with `/` separators on every OS. The CLI
takes a full key, any unique suffix, or a glob. Keys are path-qualified because filename-only keys collide across
folders.

## MSBuild integration

Core ships `buildTransitive/Nullean.Snapshots.targets`, so every package that depends on core brings it along:

- `SnapshotsDirectory` (default `$(MSBuildProjectDirectory)/.snapshots`)
- `SnapshotsReceivedDirectory` (default as above)
- `SnapshotsIncludeInProject` (default `true`): adds the verified files as `None` items so they show in the IDE even
  though default globbing skips dot folders. Set it to `false` to opt out.
- The project directory, project name and both directories are written into `[AssemblyMetadata]`, so a test run
  never has to guess a root. Without them (a test assembly built some other way) the fallback walks up from
  `[CallerFilePath]` to the nearest `*.csproj` and uses `.snapshots/` and `obj/snapshots/received/`.

## CLI discovery

`snap` does not glob for snapshot folders. From the working directory (or `--root`) it probes
`artifacts/snapshots/received/` and `.artifacts/snapshots/received/`, then each directory holding a `*.csproj` for
`obj/snapshots/received/`, skipping `.git`, `bin`, `obj` and `node_modules` while walking. Each received file maps back
to its verified file by the prefix swap.

## Dependencies

- TUnit and AwesomeAssertions: the latest stable release only (1.78.0 and 9.6.0 at the start), with no older-version
  test matrix. Packages that depend on them carry that as their minimum.

## Repository

- **Plain `dotnet` commands in CI rather than curb's F# build project.** Curb's scripts exist for corpus gates this
  repository does not have; the pack matrix needs only `dotnet pack -r <rid>`. Revisit if the workflow grows logic.
- **No strong naming.** Nothing here needs it, and `InternalsVisibleTo` stays a plain assembly name.
- **Tests run on Microsoft.Testing.Platform** (`global.json` `test.runner`), so `dotnet test --project ...
  --treenode-filter ...` works, which the failure message prints.
