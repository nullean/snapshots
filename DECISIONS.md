# Decisions

Non-obvious choices, newest last. Each says what was chosen, and why when the why is not obvious.

## Fixed at the start

- Name `Nullean.Snapshots`, CLI command `snap`, MIT. .NET 10.
- Built on AwesomeAssertions.
- Never auto-accept. Accept with `snap accept` or `SNAPSHOT_ACCEPT=1`; with `CI=true` nothing is ever accepted.
- We own the JSON/YAML tree diff. `SystemTextJson.JsonDiffPatch` is a test-only oracle, never a runtime dependency.
- The CLI is a native AOT dotnet tool and works on files only; it references no format package. Two exceptions:
  `snap journey run` references `.Browser` and starts Chrome (see "Browser"), and `snap http run` references `.Http`
  and sends HTTP requests (see "HTTP").
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
- **Injected fixtures are not arguments.** When TUnit reports that a side's arguments (`Metadata.ClassDataSource` or
  `.MethodDataSource`) came from a `ClassDataSource` attribute, any arity, that side is left out of the hash. A
  `ClassDataSource` constructs an instance of a type; it is a dependency, not data, and it would only be hashed through
  `ToString()`, its type name, so moving the fixture's namespace would orphan every snapshot of the class (#13).
  Detected from TUnit's metadata rather than a flag or a "skip objects without `ToString()`" rule, which would rename
  existing data-driven snapshots and guess where TUnit knows. One exception: when the same level declares more than one
  data source (two `ClassDataSource` attributes make two variants of each test), the arguments stay in, because the
  injected instance is all that tells the variants apart. The count comes from `GetCustomAttributes` on the class and on
  the method (reached through its first parameter); TUnit's own attribute list collapses attributes of the same type.
- **Duplicates.** A key is owned by the first test id and call-site line that uses it in the run. Another owner fails
  with "pass a name"; the same owner again (a retried test) is fine.
- **Deterministic builds.** A `[CallerFilePath]` like `/_/tests/Acme.Tests/Rendering/X.cs` is mapped back by matching
  its leading folders against the end of the project directory from the assembly metadata.
- **Snapshots of a file.** `Snapshot.MatchFile` keys a snapshot by a source file instead of the calling test:
  `{Project}/{dir}/{file name without extension}`, the file's place in the project. It exists for browser journeys,
  which a test and the CLI both run and must compare against one verified file. A relative path resolves against the
  test project's directory; a file outside the project is an error rather than a key that could collide. The calling
  test still owns the key for the duplicate check and still supplies the `--treenode-filter` line; without a test the
  message leaves that line out.
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
- **The brand** ("Aperture", `docs/images/brand`, from the design kit): frost `#9FC9EA` for the approved baseline and
  lime `#C6DD7A` for what was added, a six-blade shutter iris around a lime point of light, and the `snapshots.`
  lockup. The docs theme is curb's with its palette remapped to these; warning colours stay amber. In the light theme
  the brand's `#2884CC` and `#A0C32D` are 3.8:1 and 1.9:1 on paper, so link and highlight text use `#1D6AA8` (5.4:1)
  and `#5B7413` (5.0:1); the frames keep the brand hues.
- **Inlined lockups.** The wordmark is live JetBrains Mono text, which an SVG cannot render with the page's fonts when
  loaded through `<img>`, so the landing page and the docs header (`scripts/docs-pages.py`) inline the animated
  lockup instead. The PNGs (NuGet icon, favicons, social card, README lockups) are rendered from the SVGs on a
  transparent canvas with JetBrains Mono embedded; the README uses absolute image URLs so it renders on nuget.org too.
- **`scripts/docs.sh` does not download docs-builder**: it takes `DOCS_BUILDER` or a `docs-builder` on the PATH.

## Binary snapshots

- **A second path, not a flag on the text one.** `Snapshot.Match(ReadOnlyMemory<byte>, BinarySnapshotFormat, ...)`
  shares key resolution, key claims, the manifest, accept modes and the message with text, but never scrubs or
  normalises: the bytes are stored exactly as given. A format's `Validate` throws before anything is written.
- **Attachments** are extra received files a format writes on a failure, such as a diff image, named
  `{name}.received-{suffix}` (`Draws.received-diff.png`). The dash keeps `.received.` out of the name, so discovery
  never lists one as pending. A format declares its suffixes up front, so a pass or an accept deletes stale ones without
  listing the folder; returning an undeclared one is an error. The message lists each under its label after
  `Received:`.
- **The CLI tells binary from text by content, as git does**: a NUL in the first 8000 bytes. It still references no
  format package, so a format from another package works too. A binary snapshot is accepted byte for byte (normalising
  line endings would corrupt it), `diff` and `review` show paths, sizes and attachments instead of a line diff, and
  accept and reject delete the attachments with the received file.
- **`.gitattributes`:** this repository's `*.verified.* text eol=lf` would have made git treat a `.verified.png` as
  text, so `*.verified.png` and `*.received.png` are marked `binary` after it. The image docs tell users with a
  similar rule to do the same.

## Images

- **Our own PNG codec, PNG only.** A decoder in managed integer code (zlib from `ZLibStream`, the five scanline
  filters, every colour type and depth, `tRNS`, Adam7) gives the same RGBA on every OS and architecture and adds no
  dependency. ImageSharp's Split License would pass on to users of an MIT package; SkiaSharp ships native binaries
  and decodes through platform code that can differ between machines. Every chunk CRC is checked; ancillary chunks
  and colour management are ignored; 16-bit samples keep their high byte, as stb_image does. 8192 × 8192 pixels at
  most, so 16-bit data stays within one array. Encoding (diff images only) is 8-bit RGBA with the Up filter and our
  own CRC table, rather than `System.IO.Hashing`.
- **The comparison is pixelmatch's** (ISC, Mapbox), ported: the YIQ distance with `35215 * threshold²` as the limit,
  anti-aliasing detection on the 3×3 neighbourhood with the "many siblings" test in both images, and translucent
  pixels blended over a background that varies by position. Two departures: the background pattern is keyed by pixel
  index (pixelmatch's byte offset is always even, so its red channel never varies), and the full delta is unsigned,
  since only pixelmatch's second diff colour used the sign. An edge that moves one pixel reads as anti-aliasing, as in
  pixelmatch; two pixels is a difference.
- **Allowance:** `MaxDifferentPixels` and `MaxDifferentRatio`, both 0 by default; with both set the larger applies,
  so setting one never makes the other stricter.
- **Regions** group differences by 8×8 cells that touch, diagonals included, so a changed word reads as one region
  rather than one per glyph, and grouping costs one pass over the cells.
- **Oracles:** PngSuite (free to copy, vendored with its licence) decoded by StbImageSharp, a C# port of stb_image,
  test-only like the JSON oracle. `Validate` decodes the whole actual image before anything is written, so it is
  decoded twice on a comparison; a screenshot takes far longer than that.

## Browser

- **The browser is Nullean.Cdp**, a CDP client in its own repository (nullean/cdp): no Playwright, no Node, no
  daemon, AOT-clean. Journeys are agent-browser's commands, so agents explore with agent-browser and write the steps
  down; Nullean.Cdp parses, runs and exports them. This repository only snapshots what a run reads. Its docs are at
  cdp.nullean.net.
- **A snapshot per journey file.** `Snapshot.MatchFile` keys it by the journey's path, so a test and `snap journey run`
  share `.snapshots/{dir}/{name}.verified.journey`. One transcript per journey, not a file per read: inserting a step
  is one inserted section, not renamed files.
- **The transcript:** a section per read, `## {normalised command}`, `url: {url}`, then the output. Snapshots print the
  accessibility tree as Nullean.Cdp prints it; every other read is quoted with `> ` on each line, so page text can
  neither start a section nor pass for a tree. Headings carry no line numbers.
- **Compared as trees.** The transcript parses into `[{step, url, tree | text}]`, a tree node into `{role, name,
  attrs, value, children}` with empty parts left out (short paths: `$[0].tree[0].children[3].name`), attribute flags as
  `true` and values as strings. TreeDiffer's LCS makes an inserted section or list item one difference. Positions point
  into the verified file, and the line diff follows within the usual caps. `.aria` is the same tree for one page.
- **Scrubbing:** the base URL's root becomes `{origin}`: the origin of an http(s) URL, the whole folder of a `file:`
  URL (which differs per machine).
- **A failed step fails the assertion** even when the partial transcript matches, with `Journey stopped at
  {file}:{line}: {command}` and the error first. The received file holds the reads before the failure.
- **One Chrome per test process** (`Lazy<Task<Browser>>`, disposed on `ProcessExit`), a context per journey.
  `JourneySnapshot.Find` lists `*.journey` under a project folder for a data source; the project comes from the
  assembly metadata the build targets write, else the nearest `*.csproj` above the caller.
- **`.Browser.Assertions`** holds `MatchJourneySnapshot` and `MatchAriaSnapshot`, apart from `.Browser`, so the AOT CLI
  references the runner without AwesomeAssertions.
- **`snap journey run`** compares through the same engine with no test identity. It never accepts (`SNAPSHOT_ACCEPT` is
  hidden from it and it says so) and never writes the run manifest: a run of some journeys would make every other
  snapshot look orphaned. The project is always the one holding the journey (walking up to its csproj), never the
  process's assembly; its received folder is the artifacts one when that already exists, else `obj/`. Globs skip
  `bin`, `obj`, `node_modules` and dot folders, since verified transcripts end in `.journey` too, and `**/` matches no
  folder as well. Exit codes: 0, 1 differs or a step failed, 2 usage or journey errors, 3 Chrome could not start.
- **A test case per journey file, from a data source, not a source generator.** `[JourneyFiles(folder)]`
  (Nullean.Snapshots.TUnit; `[SnapshotFiles(folder, pattern)]` underneath) yields a `SnapshotFile` per file whose
  `ToString` is its name, so cases read `Run(basket/empty)`. A generator emitting a `[Test]` per file was built and
  dropped: TUnit discovers tests with its own source generator, generators cannot see each other's output, and the
  generated tests compiled but were never discovered. TUnit's reflection mode would see them, but it costs the whole
  test project TUnit's AOT discovery.
- **Setup per folder:** a data source leaves out files under a folder that another data source of the same pattern
  names more specifically, found by scanning the test assembly's methods once (cached per assembly). That scan
  suppresses the trimming warnings, with the reason: test assemblies are not trimmed and TUnit keeps every test
  method. Each file thus belongs to one test, as `MatchFile`'s key ownership requires.
- **Options pass through to Nullean.Cdp:** `AllowedHosts`, `FileRoot`, `ArtifactsDirectory` and `Secrets` (the
  process environment when unset), and on the CLI `--allow`, `--file-root` and `--artifacts`.
- **Files a journey writes** (`screenshot`, `network har stop`) go into `Snapshot.ArtifactsDirectory(journey)` unless
  a folder is given: `{received}/{folder}/{name}.artifacts`, next to the received transcript, emptied before each run
  so nothing from an earlier run lingers. Inside the received folder, so the library still writes nowhere else. The
  `.artifacts` suffix keeps it apart from a test class's folder of the same name, whose received files emptying it
  would otherwise delete; class names have no dots. Only journeys with such a step get a folder. Discovery ignores the
  files in it, since only `.received.` names are received snapshots.
- **Checks belong in transcripts when their output is stable:** `a11y`, `links` and `network requests` print
  deterministic lists (same-site links and request URLs are scrubbed like everything else), `errors` an empty line or
  the errors. Raw `timings`, `vitals` and `metrics` change every run; with `--budget` they print only whether each
  reading is within its limit.
- **Assertions are not transcript.** An `expect` line (Nullean.Cdp 0.6) records nothing when it passes, so adding one to a
  journey changes no verified file, and a failure is an ordinary stopped step: `Journey stopped at {file}:{line}: expect …`
  and the assertion's own message, with the reads before it in the received transcript. Nothing in this repository is
  assertion-aware; `LiveJourneyTests` pins it (the live `shop.journey` carries `expect` lines and its verified transcript
  is unchanged; a failing assertion through `JourneySnapshot.RunAsync` and through `snap journey run`, exit code 1).
  The same holds for the assertions about the page rather than an element (Nullean.Cdp 0.7): `expect request` (a finished
  request matching a URL glob, with filters on method, status, and the request body's size and text) and `expect
  same-document`. `telemetry-journeys/telemetry.journey` runs both against `TelemetryServer`, a page that posts JSON to a
  collector after a short delay, as a browser telemetry SDK's batch processor does, and its verified transcript is only the
  final `errors` read; `cli-journeys/telemetry-fails.journey` pins the failure through both entry points. These journeys sit
  apart from `journeys/` because they need an HTTP server, where that folder's base URL is the `file://` site; a failure
  message shows a request body's size and never its text, so a payload with secrets does not reach a log or a received file.
  **An export started while the page unloads is invisible:** a `fetch` (keepalive or not) or `sendBeacon` in `pagehide` or
  `visibilitychange` reaches the server, but Chrome's DevTools protocol reports no request for it, to Nullean.Cdp or to
  Playwright (checked against a local server with both), so no journey can assert on it. The fixture therefore flushes on a
  timer while the page is alive. `open about:blank` is also refused by the journey runner ("about URLs are not allowed"),
  so leaving a page means opening another one.
- **Journeys on Playwright and Synthetics are documented, not owned here.** The npm package `@nullean/journeys` lives in
  nullean/cdp; its Playwright snapshot trees differ from Nullean.Cdp's, so a transcript is only ever compared from the .NET
  run.

## File system access

All snapshot IO goes through `System.IO.Abstractions`, wrapped in `Nullean.ScopedFileSystem`:

- **Library:** scoped to the project's verified and received folders (`.snapshots` and `.artifacts` allowed as hidden
  folders, `.snapshots-project` as a hidden file). Finding the project (assembly metadata, the csproj walk-up, the
  `.git` lookup) reads outside that scope and never writes.
- **CLI:** scoped to `--root`. A marker whose verified folder lies outside the root is skipped with a warning instead
  of widening the scope, so a stray marker cannot point writes elsewhere.
- Writes are atomic: a temporary file in the same folder, then a move.
- **The one exception is inline snapshots** (see "Inline snapshots"): they edit a test's `.cs` file. The library does it
  only under `SNAPSHOT_ACCEPT=1` (never in CI), through a file system scoped to the test project's directory, and only
  for `.cs` files; the CLI only inside `--root`, and only for `.cs` files.

## Dependencies

- TUnit and AwesomeAssertions: the latest stable release only (1.78.0 and 9.6.0 at the start), with no older-version
  test matrix. Packages that depend on them carry that as their minimum.

## Repository

- **Plain `dotnet` commands in CI rather than curb's F# build project.** Curb's scripts exist for corpus gates this
  repository does not have; the pack matrix needs only `dotnet pack -r <rid>`. Revisit if the workflow grows logic.
- **Nightly properties.** `nightly.yml` runs the whole suite with `CsCheck_Iter=10000` on linux, windows and macos
  once a day (and by hand); pull requests run CsCheck's default 100. A failing property prints its seed: reproduce
  with `CsCheck_Seed=<seed>` and keep the case as a regression test.
- **CI split.** Pull requests build, test and pack the libraries on linux, windows and macos. Pushes to main, tags and
  manual runs also pack the native tool for every RID on a matching runner (including the arm64 runners), pack the
  root and portable `any` packages, and install the tool from a local feed on each OS to run `build/smoke-cli.sh`,
  installed and through `dnx`. A plain `dotnet pack` of the tool emits managed per-RID packages too; those are dropped,
  as curb does, so only the AOT ones ship.
- **Releasing.** Push a tag (`0.1.0-alpha.1`: MinVer takes the version from it), as nullean/entities and nullean/curb
  do. The `release` job runs for a pushed tag, needs every other job including the smoke tests, and checks that every
  package carries the tag's version. Pushing the tag is the release decision, so tag only what is meant to ship; a
  failed run is fixed and re-run (the pushes skip what is already there). It was a manual run on the tag until the
  repositories were aligned. The `release` environment has no reviewers (it stays because the nuget.org trusted
  publishing policy names it, and only tags may deploy to it). It publishes with nuget.org trusted publishing
  (`NuGet/login`, the org's `NUGET_USER`), so no API key is stored. The root tool package is pushed last, after the
  per-RID and `any` packages it points at. Once the packages are on nuget.org the job creates the
  GitHub release for the tag (`gh release create --generate-notes`, notes from the pull requests since the previous
  tag, the packages attached, a `-` version marked as a prerelease); the job needs `contents: write` for it.
- **No strong naming.** Nothing here needs it, and `InternalsVisibleTo` stays a plain assembly name.
- **Tests run on Microsoft.Testing.Platform** (`global.json` `test.runner`), so `dotnet test --project ...
  --treenode-filter ...` works, which the failure message prints.

## HTTP file parser

`Nullean.HttpFileParser` parses and runs `.http` files on its own, with no dependency on the snapshot packages and none
beyond the BCL; `Nullean.Snapshots.Http` will build on it. It packs its own README rather than the repository's.

- **The macOS job has failed in two different ways, and only one is ours.** (1) It never got a runner: no steps, an empty
  runner name, cancelled at exactly 15 minutes. That is GitHub's macOS pool being full (6 of the last ~30 macOS jobs);
  only a re-run helps. (2) It had a runner and the Test step ran until the job's timeout (6 jobs, cancelled at 1500 s),
  most likely the Chrome-driven tests (cdp's workflow records the same on small macOS runners, and runs browser tests on
  Linux only). That could not be confirmed: GitHub discards the log of a cancelled job. The Test step has its own 10-minute
  timeout so the next hang fails with its log. The browser tests are never skipped: without Chrome, or when it cannot be
  started, they fail (the runner images have Chrome, and a skipped test would hide exactly that).
  The timeout did not capture a log: on a later freeze the Test step ran 17 minutes past its 10-minute limit, so the whole
  VM stops answering and the agent cannot enforce it. Memory was ruled out as the obvious cause (five parallel Chromes
  became two with no change in peak memory). So on macOS the Chrome-driven tests run in their own job, `browser-macos`:
  still on macOS, still failing if Chrome is missing or cannot start, but outside the release job's `needs` and with
  `continue-on-error`, so a frozen runner cannot hold up a release or fail the workflow. Every other test project runs on
  every OS, one project at a time. This is also the experiment: if the gating macOS job still freezes without the browser
  tests, they were not the cause. Six runs of the change answered it: the gating macOS job (browser tests out) passed every time (77 to 120 s), and the
  browser job froze three times in six (the three that finished took 53 to 83 s), so the freeze is in the Chrome-driven tests.
- **The browser tests use two Chromes, not five.** Four `LiveJourneyTests` each launched their own, next to the one
  `JourneySnapshot` shares, and TUnit runs them in parallel. Three now run through the shared one; the accessibility-tree test,
  which needs a page, uses one shared browser of its own. Measured locally this cuts the Chrome instances running at once from
  5 to 2, but not the memory (about 3 GB either way: it is the concurrent pages and renderers), so it is **not** a fix for the
  macOS freeze; the hypothesis that five Chromes exhaust the 7 GB runner did not hold. What it plausibly helps is the other
  flake, Chrome not opening its DevTools port within 30 s when several start together. Evidence that the freeze is the whole
  VM and not one test: the Test step's 10-minute timeout did not fire on a macOS job that ran for 17 minutes.
- **Test servers never share a port with a refusal test.** `TestServer.FreePort()` binds port 0 and releases it, so a port it
  returns can be handed to another test's server before the first test uses it. A test that expects a refused connection
  therefore uses `TestServer.RefusedPort` (port 1: well-known, outside the range ports are handed out from, refused on every
  platform) and never a released port; this was the intermittent `AConnectionFailureStopsTheRunAndKeepsEarlierExchanges`
  (`Succeeded` true: another test's server was listening on the "closed" port). A bound socket that never listens is not
  an alternative: it refuses on Linux but macOS drops the connection and the connect times out after about 8 s. A refusal
  is not instant everywhere either: Windows takes a few seconds to report one, so no test asserts how fast it is, only that
  it is a `ConnectionRefused` and not a timeout.
  `TestServer` itself picks another port if the one it chose is taken before it binds.
- **Dialect: the portable subset.** What Visual Studio 17.8+ and the VS Code REST Client share, plus JetBrains'
  script-free forms: `### name` separators, indented URL continuation lines, `$uuid`/`$random.uuid`/`$isoTimestamp`,
  `$env.NAME`, `@timeout` with a unit, multipart bodies mixing text and `< ./file` lines (joined with CRLF, as
  RFC 2046 needs). Custom `$datetime` formats are .NET format strings, as in Visual Studio (REST Client uses Day.js),
  and `iso8601` is .NET's round-trip format. `$randomInt` with no arguments is any non-negative `int`.
- **Refused, with the line and a reason:** JetBrains scripts (`> {% %}`, `< {% %}`, `> handler.js`) and `run`/`import`
  statements, because they need a JavaScript engine; response redirection (`>> file`), because the result holds the
  response; `@prompt`, because a run cannot ask; `$aadToken`, because it needs an interactive sign-in (use
  `{{$secret Name}}`); GraphQL, WebSocket and gRPC requests; cURL commands; XPath. Unknown `@directives` are plain
  comments, unknown `$dynamic` variables are errors.
- **Errors.** Parsing collects every problem and throws one `HttpFileParseException` listing them as
  `file:line: message`. `HttpFile.Validate` reports, without sending anything, variables used before their definition
  or never defined, references to requests that are not earlier in the file and missing body files. Resolution errors
  during a run name the line of the reference. A `{{` without `}}` on the same line is literal text.
- **`/`-relative URLs** resolve against `HttpClient.BaseAddress`, our extension: one file runs against a
  `WebApplicationFactory` client and, through `{{host}}`, against a real host. A URL with no scheme gets `http://`.
- **Redirects are followed by the runner,** because `AllowAutoRedirect` is fixed per handler and `@no-redirect` is per
  request. Up to `MaxRedirects` (10); 303, and 301/302 after a POST, become a body-less GET; `Authorization` and
  `Cookie` are not sent to another origin. Callers should pass clients with `AllowAutoRedirect = false`; a client that
  follows redirects itself makes `@no-redirect` ineffective, which is documented rather than detected.
- **Compression.** The runner's own client decompresses (`AutomaticDecompression = All`). For a caller's client, a body
  still carrying `Content-Encoding: gzip|deflate|br` is decoded by the runner (deflate with or without the zlib
  wrapper) and the exchange records the encoding it undid. Headers are kept as received.
- **Cookies.** The runner's own client keeps a cookie jar the runner manages, so `@no-cookie-jar` works per request. A
  caller's client keeps cookies as its handler does; the runner adds none, to avoid sending them twice.
- **`{{$secret Name}}`** is our extension: a secret from `HttpFileRunOptions.Secrets`, for values with no environment
  file entry (tokens a test fixture obtains, for example).
- **User secrets lookup.** For `AspnetUserSecrets`, `HttpFileRunOptions.Secrets` is asked first. Then the user secrets
  id comes from `UserSecretsId`, else from an attribute named
  `Microsoft.Extensions.Configuration.UserSecrets.UserSecretsIdAttribute` on `SecretsAssembly`, read through
  `CustomAttributeData` so no package is referenced, else from `<UserSecretsId>` in the nearest `*.csproj` above the
  `.http` file. `secrets.json` is read from `%APPDATA%\Microsoft\UserSecrets\{id}` on Windows and
  `~/.microsoft/usersecrets/{id}` elsewhere (the root is injectable) and flattened to `Section:Key`, case-insensitive,
  as Microsoft configuration does. Other providers (`AzureKeyVault`, `Encrypted`) come only from `Secrets`.
- **Environments.** The nearest folder at or above the `.http` file holding `http-client.env.json` or one of its
  override files. Layering: `$shared` from the main, `.private` then `.user` file, then the named environment from the
  same files in the same order. File variables beat the environment; `HttpFileRunOptions.Variables` beats both.
  `$dotenv` reads the nearest `.env` defining the name, from the `.http` file's folder up to the nearest folder with a
  project file.
- **Redaction.** Every value that came from a secret, `$processEnv`, `$env`, `$dotenv` or a `.user`/`.private`
  environment file is recorded with the reference that produced it (`{{$processEnv API_KEY}}`, `{{ApiKey}}`,
  `{{$secret Name}}`), and its URL-escaped form too. `Redact` replaces the longest value first, so a value containing
  another is not half-replaced. Failure messages are redacted when created; exchanges keep the raw wire data and
  `Redact(exchange)` gives a redacted copy (URI, headers, and bodies that are UTF-8 text). Values shorter than 4
  characters fail the run: replacing them everywhere would mangle unrelated text. `ToString` on a recorded value and
  on an environment value never prints the value.
- **Determinism.** Time comes from `HttpFileRunOptions.TimeProvider` and randomness from `HttpFileRunOptions.Random`.
  `$guid` is built from the `Random`'s bytes with the version 4 bits set, so a seeded run repeats.

## HTTP

`Nullean.Snapshots.Http` snapshots HTTP responses and runs `.http` files with `Nullean.HttpFileParser`;
`.Http.Assertions` holds the AwesomeAssertions extensions, apart from it, so the AOT CLI references the runner without
AwesomeAssertions (as `.Browser` and `.Browser.Assertions` are split).

- **One format, `.responses`, named "HTTP",** for a single response and for a file's run: a section per response,
  `## {heading}`, `status: {code} {reason}`, the chosen headers, a blank line and the body. A file's heading is the
  request as written (`login: POST {{host}}/api/login`), variables unresolved, so hosts and secrets never reach it and a
  host change renames nothing; a single response's is its method, path and query, or `response` with no request.
- **Headers are an allow-list:** `content-type` and `location`, plus what `Headers` names, lower case, sorted, several
  values joined with `, `. Dates, request ids and server names change between runs, so writing every header would
  make every snapshot need scrubbers. `authorization`, `cookie`, `set-cookie` and `proxy-authorization` are never
  written, even when named: they carry credentials, and a snapshot is committed.
- **Bodies are decoded first** (the runner decodes, and `RenderAsync` undoes gzip, deflate and br a handler left in
  place), so compression never shows; `content-encoding` is written only when chosen. An encoding it cannot undo makes
  the body binary.
- **Three kinds of body, by media type.** JSON (`application/json`, `+json`) through `JsonTree.Pretty`, written raw:
  its lines start with whitespace, a bracket, a quote or a scalar, never `## `. Text (`text/*`, XML, JavaScript,
  YAML, other JSON types such as NDJSON, form posts, anything with a charset) quoted with `> ` on every line, as journey
  reads are, so a body can never start a section. Anything else is `binary: {n} bytes, {media type}`: bytes in a
  committed text file help nobody. Invalid JSON with a JSON type is text, so it can still be read.
- **Parsed into `[{request, status, headers, body | text | binary}]`,** empty parts left out. The three body kinds are
  different keys rather than three shapes under `body`, so a JSON string and the same text are not equal. The header
  map compares in any order; a JSON body's spans are moved to its lines in the whole file. Compared by TreeDiffer, then
  the line diff, as journeys are.
- **Redacted twice.** Each exchange goes through `HttpFileRunResult.Redact(exchange)` before rendering, as the parser
  intends, and the rendered text goes through `Redact(text)` again: pretty-printing unescapes JSON strings, so a value a
  server echoed as `\u002B…` (System.Text.Json escapes `+`, common in tokens) reads as the value only after rendering.
  The failure message is redacted too.
- **A request that stops the run fails the assertion** even when the partial transcript matches, with `Request
  stopped at {file}:{line}: {request}` and the message first, as a failed journey step does. Error statuses are
  responses.
- **`.http` files are keyed by `Snapshot.MatchFile`**, so a test and `snap http run` share
  `.snapshots/{dir}/{name}.verified.responses`. `[HttpFiles(folder = "http")]` is `[SnapshotFiles(folder, "*.http")]`.
  A single response (`MatchHttpSnapshot`) is keyed by the test, like any snapshot.
- **`snap http run`** is `snap journey run`'s shape: the runner's own client (the parser's, which follows redirects
  itself, keeps a cookie jar and decompresses), never accepts, never writes the manifest, globs over `*.http` skipping
  `bin`, `obj`, `node_modules` and dot folders. `--env`, `--var name=value` and `--header` match the test options.
  Exit codes: 0, 1 differs or a request failed, 2 usage or parse errors, 3 when a `.http` file, environment file or
  user secrets file cannot be read (an `IOException` or `UnauthorizedAccessException`); unlike Chrome failing to
  start, that is per file, so the other files still run. It adds no trim or AOT warnings to the published tool.
- **Tests run a real server**: an `HttpListener` on a free port (as the parser's tests do) and, once, ASP.NET Core's
  in-memory `TestServer` (`Microsoft.AspNetCore.TestHost` 10.0.0, test-only) for the `WebApplicationFactory` path.

## Inline snapshots

`MatchInlineSnapshot` (and `MatchInlineJsonSnapshot`, `MatchInlineYamlSnapshot`, `MatchInlineHtmlSnapshot`) keep the
expected value as a string literal in the test; accepting rewrites that literal. Text, JSON, YAML and HTML only: an
image or a transcript is not something to read in a source file.

- **Finding the literal needs no Roslyn.** The compiler fills in `[CallerLineNumber]` and
  `[CallerArgumentExpression(nameof(expected))]`; the source text of the argument says which of a call's arguments is the
  literal (a named `expected:`, the second argument of the format overload, one of two calls on a line), and the method
  name and line say which call. The line is the one of the `.MatchInlineSnapshot(` name for a chain over several lines
  (checked against the real compiler, with a test that reads its own source), and the finder also accepts any line of
  the call or its statement. A small C# lexer (comments, characters, regular, verbatim, interpolated and raw strings)
  does the rest, because `snap` is a native AOT tool and Roslyn would be megabytes. Roslyn is a test-only oracle
  (`Microsoft.CodeAnalysis.CSharp`): a property test checks that what the rewriter writes parses to the same value, and
  that our reader agrees with Roslyn on literals it did not write.
- **Wrappers must be called directly from the test**, with a parameter named `expected`: the finder searches the source
  for the wrapper's method name (`InlineSnapshotCallSite.MethodName`). A helper that forwards to `MatchInlineSnapshot`
  would put the line of the helper, not the test, in the record.
- **No verified file; a received record.** A failing inline snapshot writes `{key}.L{line}.received.inline`, JSON with
  the format, method, test file (relative to the received folder, as the marker's `verified=` is), line, the argument's
  text, the value the literal held (null when left out) and the value to write. It sits in the received tree, so discovery,
  `reject`, the `.tmp` rule and `snap status` need nothing new; `PendingSnapshot.VerifiedPath` is the test file, which is
  where accepting writes. The key is `{Project}/{dir}/{Class}/{Method}.L{line}[.{argsHash}]`: the line tells two calls in
  one test apart without a name, which a literal in the source could not carry. The project still gets a run manifest
  (with no entries), so a project of only inline snapshots does not read as "no manifest" in `snap orphans`.
- **The expected literal is not scrubbed, the actual value is.** As for a verified file. The literal is compared
  normalised (line endings, one trailing newline), so `"a"` and `"a\n"` are the same snapshot.
- **What is refused, with the reason, leaving the snapshot pending:** an expected value that is not a plain literal
  (interpolated, constant, concatenation, variable); a literal that no longer holds the value the test ran with; test
  cases of a data-driven test that disagree about one literal (all of them, since none is more right); a test file
  outside the project (library) or `--root` (CLI), or one that is not `.cs`. Cases that agree are fine.
- **A hash of the test file is in the record.** Accepting one literal can move the lines of the next (one line becomes
  five), and a later record would then locate its call by a stale line; with a repeated literal text it could land on
  the wrong call. So an accept is refused when the file's hash differs from the one the test saw ("run the tests
  again"), and everything pending for one file is applied in one rewrite from the original text, bottom-up, so the
  edits cannot disturb each other. `snap review` collects its accepts and applies them together when the walk ends.
  Without a hash (the test could not read its source) the located call, the argument text and the old value still must
  agree.
- **`SNAPSHOT_ACCEPT=1` edits at process exit,** not during the test: the literal is located and checked at the
  assertion (so a literal that cannot be rewritten fails at once with the reason, and the record is written), the edit is
  queued, and `ProcessExit` applies them, with the manifests, one write per file, atomically, keeping the file's line
  endings and byte order mark. A failure to apply writes the records back and says so on stderr. The test host may be
  running the assembly built from the old text meanwhile; that is fine, it is already loaded.
- **The literal:** a regular literal with C# escapes when the value is one line; otherwise a raw `"""` literal indented
  one level (a tab in a tab-indented file, else four spaces) past the line that holds the call, with as many quotes as
  the value's longest run of quotes needs plus one, at least three. A multi-line value with control characters, a
  separator Roslyn treats as a line break (U+0085, U+2028, U+2029) or a lone surrogate is written as a regular literal
  with escapes, because a raw literal would not read back as the same text.
- **Known limit:** a tree format reports a changed value by its position in the verified file (`(verified.json:14:5)`).
  Inline, that position is a line and column in the expected text, which the message still calls `verified.json`.
  Relabelling it needs a name on `SnapshotFormat.Compare`; not done here.

## Transcript modes and variants

- **`TranscriptMode.Off` keeps the run's own assertions and drops the comparison.** A live site or service returns text
  that drifts, so a journey or `.http` file against one fails on content nobody changed. `Off` runs everything and fails
  only when a step or request fails, an `expect` line included. It writes no received file and claims no key, so no
  verified file exists to go stale or look orphaned. It lives in core (`Nullean.Snapshots`) because the journey and
  `.http` packages share it.
- **A variant is the snapshot's `name`.** `MatchFile(..., variant)` puts it where a test's `name` goes in the key
  (`journeys/shop.preview`), so the verified file is `shop.preview.verified.journey`, the received file, the run
  manifest line and `snap accept <key>` all follow the existing rules and `snap orphans` needs no special case.
  Variants use the same name check as `name` (letters, digits, `.`, `-`, `_`, no leading or trailing dot).
- **A variant never falls back to the plain file.** A variant with no verified file of its own is a new snapshot, as
  any other missing verified file is. A fallback would let a flag-on run pass against flag-off output.
