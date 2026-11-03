# Decisions

Non-obvious choices, newest last. Each says what was chosen, and why when the why is not obvious.

## Fixed at the start

- Name `Nullean.Snapshots`, CLI command `snap`, MIT. .NET 10.
- Built on AwesomeAssertions; Verify is not used (v33+ adds a build-time sponsorship check).
- Never auto-accept. Accept with `snap accept` or `SNAPSHOT_ACCEPT=1`; with `CI=true` nothing is ever accepted.
- We own the JSON/YAML tree diff. `SystemTextJson.JsonDiffPatch` is a test-only oracle, never a runtime dependency.
- The CLI is a native AOT dotnet tool and works on files only; it references no format package.
- Libraries are AOT/trim-clean. AngleSharp, the one suspect, turned out clean too (see "HTML").

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

## HTML

- **One package, assertions included.** `Nullean.Snapshots.Html` holds the format and its AwesomeAssertions
  extensions (`MatchHtmlSnapshot`, `BeHtml`, `ContainHtml`, `NotContainHtml`) and depends on
  `Nullean.Snapshots.AwesomeAssertions`. Putting `BeHtml` in the AwesomeAssertions package, as the brief had it, would
  make every user carry AngleSharp. `MatchHtmlSnapshot` rather than a `MatchSnapshot` overload, which would be
  ambiguous with the text one when called without arguments.
- **AngleSharp 1.7.2 as the minimum**, the lowest that works rather than the latest, so a consumer that pins it (docs-builder does) is not forced to upgrade by a test package.
- **AOT-clean after all.** AngleSharp 1.7.2 and AngleSharp.Diffing 1.1.1 publish with native AOT without a single trim or
  AOT warning (spiked on osx-arm64, the binary parsed, printed and diffed). The package sets `IsAotCompatible`.
- **The pipeline:** parse, `Select(css)` (the body by default), `Remove(css)`, `RemoveAttributes(patterns)` with `*`
  wildcards on every element or `RemoveAttributesFrom(css, patterns)`, `Transform(Action<IElement>)`, then
  AngleSharp's `PrettyMarkupFormatter` with tabs and `\n`. docs-builder's leading-H1 strip and its
  `section#elastic-docs-v3` selector stay in docs-builder, as a transform and a select.
- **Top-level text is printed, escaped.** docs-builder printed only the root's element children, silently dropping
  text between them. Whitespace-only text is still dropped; other text is printed through the formatter's escaping,
  which a property test showed is needed (`&lt;a` printed raw reparsed as a tag).
- **Comparison is structural.** Two pretty texts match when AngleSharp.Diffing finds no difference, so a whitespace
  change never fails and a passing compare never rewrites the verified file. Each difference is docs-builder's
  `HtmlDiffString` label, then the expected and actual node, cut after six lines; a line diff of the pretty HTML
  follows within the same 80-line cap.
- **`ContainHtml` ignores indentation**, as docs-builder's did: both sides are pretty-printed and tabs removed, so a
  fragment matches wherever it is nested.

## Trees and JSON

- **Numbers keep their text.** A scalar number holds its source text; equality goes through `NumberValue` (sign,
  significant digits, exponent), never `double`, so `1 == 1.0 == 10e-1`, `-0 == 0`, and integers past 2^53 still
  compare exactly. `StrictNumbers` compares kind and text instead. NaN equals NaN: it is a value in a snapshot.
- **Equality is the diff finding nothing.** `TreeDiffer.DeepEquals` follows the same options, ignored paths and
  null-as-missing included, and a CsCheck property holds the two together. The structural hash used to bucket the LCS
  only ever rules equality out; with ignored paths or null-as-missing it falls back to the node type, so it stays
  sound.
- **Sequence alignment** is the LCS of equal items read from the front, matches first; above `CellCap` (4M cells) it
  is positional. In a gap between matches the i-th unmatched verified item pairs with the i-th unmatched actual item
  when they are similar (same node type; a shared equal entry or item; scalars always). A moved item reads as
  missing plus unexpected: its two places always fall in different gaps, since two equal items in one gap would have
  lengthened the LCS. (The pairing still refuses equal items, defensively; mutation testing confirms that guard is
  unreachable.)
- **Paths:** `$`, `.key` for identifier keys, `["quoted"]` otherwise, `[n]`. A paired item at a different index reads
  `$[1].v (actual $[2].v)`. Unexpected items carry their actual path. Ignore patterns add `.*`, `[*]` and `..key`.
- **Ordering** of differences follows the traversal: verified keys in order, then keys only in actual; within a
  sequence, by position along the alignment. It never depends on hash or dictionary order.
- **Messages** are one line per difference, paths padded to the widest of the first 20 (at most 40 columns), and
  missing or changed values point at the verified file: `(verified.json:14:5)`. Long or multi-line strings show the
  first differing line. Columns count characters, not UTF-8 bytes.
- **JSON** is parsed with `Utf8JsonReader` (depth 256); a property repeated in one object is an error. It is stored
  with two-space indentation in its original property order, numbers as written and non-ASCII text unescaped. An
  invalid verified file is a difference, not an exception.
- **Object snapshots** live in `.Json` (`ObjectAssertions.MatchJsonSnapshot`), with a `JsonTypeInfo<T>` overload for
  AOT and a reflection overload marked `RequiresUnreferencedCode`. `JsonSerializerOptions.Default` unless given.
- **Oracle:** `SystemTextJson.JsonDiffPatch` (`DeepEquals`, semantic) is a test-only reference. It compares numbers as
  doubles, so the agreement property leaves out numbers past double precision; our exact comparison is covered by
  example tests.

## YAML

- **SharpYaml for syntax only.** Its event parser (`Parser.CreateParser`) gives events with 0-based marks, anchors and
  tags; the tree, positions (made 1-based), anchors and merge keys are built here. It publishes with native AOT
  without warnings, so `.Yaml` is `IsAotCompatible`.
- **Our own YAML 1.2 core schema.** SharpYaml's `CoreSchema` keeps YAML 1.1 rules: it types `1_000` and `0b101` as
  integers and `017` as a float. The 1.2.2 regexes (§10.3.2) are short, so they are written out: `null`/`Null`/`NULL`/
  `~`/empty, `true`/`True`/`TRUE` and the falses, decimal, `0o` and `0x` integers, floats with `.inf` and `.nan`;
  anything else plain is a string, and `yes`, `on`, `1_000` are strings. Octal and hex integers carry their decimal
  value so `0x1F` equals `31`. Quoted and block scalars are strings; `!!str`, `!!int`, `!!float`, `!!bool`, `!!null`
  tags apply; local tags (`!foo`) are typed as if untagged.
- **Merge keys:** only a plain `<<` merges. Explicit keys win over merged ones, the first of several merged mappings
  wins, and merged entries take the place of the `<<` key. Aliases are the anchored node itself. Anchors do not
  cross documents.
- **Errors:** a repeated key in one mapping, a non-scalar key, an alias without an anchor and a merge of a non-mapping
  are `FormatException`s, as invalid YAML is.
- **Documents.** An empty stream is null. A stream of several documents compares document by document by position,
  with paths `$doc[n]...`: they are separate files in one, not items to align.
- **Stored verbatim.** `MatchYamlSnapshot` stores the text as given (comments and layout survive) after the usual
  line-ending normalisation, and invalid YAML fails the assertion before anything is written.

## Review and orphans

- **The run manifest** (`.snapshots-manifest` in each project's received folder) lists, sorted, the verified files the
  latest test process touched, under a header with its start time. Touching is a lock-free `ConcurrentDictionary`
  add with no IO; the file is written once, on `ProcessExit`, replacing the previous run's, together with the
  received folder's marker so the CLI finds projects that had no failures. A run that crashes before exiting leaves
  the previous manifest. Writing it never throws.
- **`snap orphans`** lists verified files the manifest does not name and exits 1 when there are any; `--delete`
  removes them and is the only command that deletes verified files. A filtered run produces false orphans, so the
  output says to run the whole suite first. A project without a manifest is reported on stderr and skipped.
- **`snap review`**, also bare `snap`: each pending snapshot with its diff, then accept, reject, skip or quit.
  Unknown answers are asked again; the end of input means quit. On a terminal it reads single keys; with redirected
  input one answer per line, so it can be scripted and is smoke-tested in CI. It exits 1 while anything is left
  pending.
- **Colour** only in `snap review`, only when stdout is a terminal, `NO_COLOR` is unset and `TERM` is not `dumb`.
  Failure messages and every other command stay plain.
- **No locks.** `SnapshotDefaults.AddScrubber` is a copy-on-write compare-and-swap loop; reads are a volatile read.

## Documentation

- **Built like nullean/curb's and argh's:** a `docs/` docset built by the `elastic/docs-builder` action with the
  design-system layout (`FEATURE_NAVIGATION_PREVIEW`), then `scripts/docs-finish.sh` appends the theme to the emitted
  `ds.css`, puts the header lockup in (`scripts/docs-pages.py`, copied from curb), copies the fonts and replaces
  `index.html` with the hand-written landing page. Published to GitHub Pages at `snapshots.nullean.net` (Pages from
  Actions, custom domain set on the repository, TLS at Cloudflare as for curb).
- **Pull requests build the site** and upload it as an artifact; only pushes to main deploy.
- **The hue:** a quiet teal (`#6CC7C2` dark, `#1F6461` light, links at 4.5:1 on paper) with a muted rose second
  colour, next to curb's amber and argh's lime. The theme is curb's with its palette remapped; warning colours stay
  amber, since that is what they mean. The mark is a diff, a rose line out and a teal line in, drawn rather than set
  in a font.
- **`scripts/docs.sh` does not download docs-builder**: it takes `DOCS_BUILDER` or a `docs-builder` on the PATH.

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
