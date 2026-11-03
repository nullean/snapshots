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
$(ArtifactsPath)/.snapshots/received/{Project}/{same relative path}.received.{ext}    when ArtifactsPath is set
{ProjectDir}/obj/snapshots/received/{same relative path}.received.{ext}              otherwise
```

- Both are already ignored in essentially every .NET repository, so adopting the library needs no `.gitignore` change,
  a received file cannot be committed by accident, and CI that uploads `artifacts/` gets received files for free.
- Verified and received paths map to each other by swapping the prefix.
- Under the artifacts path the folder is `.snapshots/received`, so the received tree mirrors the checked-in
  `.snapshots/` by name. The `obj/` fallback keeps `snapshots/received`.
- Each received root holds a `.snapshots-project` marker, written with the first received file: `key=value` lines
  `project=` (the project name) and `verified=` (the verified folder, relative to the marker's folder, `/`
  separators). It is how the CLI finds verified files for a received tree without evaluating MSBuild.

**Key.** `{Project}/{relative dir}/{Class}/{Method}[.{name}][.{argsHash}]`, with `/` separators on every OS. The CLI
takes a full key, any unique suffix, or a glob. Keys are path-qualified because filename-only keys collide across
folders.

## MSBuild integration

Core ships `buildTransitive/Nullean.Snapshots.targets`, so every package that depends on core brings it along:

- `SnapshotsDirectory` (default `$(MSBuildProjectDirectory)/.snapshots`)
- `SnapshotsReceivedDirectory` (default as above: `$(ArtifactsPath)/.snapshots/received/$(MSBuildProjectName)` when
  `ArtifactsPath` is set, else `$(MSBuildProjectDirectory)/obj/snapshots/received`)
- `SnapshotsIncludeInProject` (default `true`): adds the verified files as `None` items so they show in the IDE even
  though default globbing skips dot folders. Set it to `false` to opt out.
- The project directory, project name and both directories are written into `[AssemblyMetadata]`, so a test run
  never has to guess a root. Without them (a test assembly built some other way) the fallback walks up from
  `[CallerFilePath]` to the nearest `*.csproj` and uses `.snapshots/` and `obj/snapshots/received/`.

## CLI discovery

`snap` does not glob for snapshot folders. From the working directory (or `--root`) it probes
`artifacts/.snapshots/received/*/` and `.artifacts/.snapshots/received/*/`, then each directory holding a `*.csproj`
for `obj/snapshots/received/`, skipping `bin`, `obj`, `node_modules` and every dot folder (`.git` included) while
walking. Each received root's `.snapshots-project` marker names its verified folder, and each received file maps back to
its verified file by the prefix swap. Half-written `*.tmp` files are ignored.

- **Keys on the command line:** a full key, a unique suffix that starts at a `/` (`RendersTable`,
  `HtmlTests/RendersTable`), or a glob where `*` stays within a segment and `**` crosses them. An ambiguous suffix is a
  usage error that lists the candidates; a glob may match many.

## Running tests

- **Identity.** With `Nullean.Snapshots.TUnit` the class folder is the runtime class, so `[InheritsTests]` keeps
  derived classes apart; the file name gets an eight hex digit FNV-1a hash of the class and method arguments' invariant
  text when there are any. The TUnit package registers itself from a module initializer it compiles *into the test
  project* (`buildTransitive`), because a module initializer in the package's own assembly runs only once something
  touches that assembly. `SnapshotsTUnitAutoRegister=false` opts out. Without the adapter the class folder is the test
  file's name and data-driven cases cannot be told apart.
- **Duplicates.** A key is owned by the first test id and call-site line that uses it in the run. Another owner fails
  with "pass a name"; the same owner again (a retried test) is fine.
- **Deterministic builds.** A `[CallerFilePath]` like `/_/tests/Acme.Tests/Rendering/X.cs` is mapped back by matching
  its leading folders against the end of the project directory from the assembly metadata.
- **Runs never prune.** The lost first version deleted stale received folders at the start of a run, which raced with
  parallel tests writing into them. A passing assertion deletes only its own received file; cleaning up is
  `snap reject`'s job.
- **Scrubbers are opt-in**, globally (`SnapshotDefaults.AddScrubber`) or per call. Line endings are always normalised;
  that is not a scrubber.
- **Message caps.** At most 20 differences and 80 lines. A difference that does not fit in the remaining lines is
  left out whole rather than cut mid-hunk, except a first difference longer than the cap on its own, which is cut so
  something shows. Either way it counts in "(+N more, see received)".
- **AwesomeAssertions.** The message is passed as a `{0}` argument with a verbatim value formatter, because 9.x reads
  braces in a format string as placeholders. `because` is placed in the first line by the library, not by
  AwesomeAssertions' `{reason}`, so the template stays one shape. Object snapshots (`ObjectAssertions`) wait for
  `.Json`, which owns serialisation.

## File system access

All snapshot IO goes through `System.IO.Abstractions`, wrapped in `Nullean.ScopedFileSystem`:

- **Library:** scoped to the project's verified and received folders (`.snapshots` and `.artifacts` allowed as hidden
  folders, `.snapshots-project` as a hidden file). Finding the project (assembly metadata, the csproj walk-up, the
  `.git` lookup) reads outside that scope and never writes.
- **CLI:** scoped to `--root`. A marker whose verified folder lies outside the root is skipped with a warning instead
  of widening the scope, so a stray marker cannot point writes elsewhere.
- Writes are atomic: a temporary file in the same folder, then a move.

## Dependencies

- TUnit and AwesomeAssertions: the latest stable release only (1.78.0 and 9.6.0 at the start), with no older-version
  test matrix. Packages that depend on them carry that as their minimum.

## Repository

- **Plain `dotnet` commands in CI rather than curb's F# build project.** Curb's scripts exist for corpus gates this
  repository does not have; the pack matrix needs only `dotnet pack -r <rid>`. Revisit if the workflow grows logic.
- **CI split.** Pull requests build, test and pack the libraries on linux, windows and macos. Pushes to main, tags and
  manual runs also pack the native tool for every RID on a matching runner (including the arm64 runners), pack the
  root and portable `any` packages, and install the tool from a local feed on each OS to run `build/smoke-cli.sh`,
  installed and through `dnx`. A plain `dotnet pack` of the tool emits managed per-RID packages too; those are dropped,
  as curb does, so only the AOT ones ship.
- **Releasing.** Push a tag (`0.1.0-alpha.1`: MinVer takes the version from it), then start CI by hand on that tag
  (`gh workflow run ci.yml --ref 0.1.0-alpha.1`). The `release` job runs only for such a manual run, needs every other
  job including the smoke tests, checks that every package carries the tag's version, and waits for a reviewer on the
  `release` environment, which only tags may deploy to. It publishes with nuget.org trusted publishing (`NuGet/login`,
  the org's `NUGET_USER`), so no API key is stored. The root tool package is pushed last, after the per-RID and `any`
  packages it points at. A tag push alone never publishes.
- **No strong naming.** Nothing here needs it, and `InternalsVisibleTo` stays a plain assembly name.
- **Tests run on Microsoft.Testing.Platform** (`global.json` `test.runner`), so `dotnet test --project ...
  --treenode-filter ...` works, which the failure message prints.
