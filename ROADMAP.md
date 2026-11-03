# Roadmap

The build brief this repository was started from, kept here so the plan does not live only in a chat session.
Where it and [DECISIONS.md](DECISIONS.md) disagree, DECISIONS.md wins; the main differences are listed first.

## Superseded by DECISIONS.md

- **Layout.** Not `__snapshots__/` next to each test file: verified files live under `{Project}/.snapshots/`,
  mirroring the test file's folder, with a folder per runtime class. Received files live under
  `$(ArtifactsPath)/.snapshots/received/{Project}/` or `{Project}/obj/snapshots/received/`.
- **CLI discovery** probes those received roots instead of globbing for `__snapshots__`.
- **TUnit and AwesomeAssertions:** the latest stable release only (1.78 and 9.6), no 1.65 test matrix.
- **Full AOT pack matrix** runs on pushes to main, tags and manual runs; pull requests run build, test and pack.

## Goal

Lightweight snapshot (approval) testing on AwesomeAssertions for HTML, JSON, YAML and plain text, plus a small native
AOT CLI, `snap`. It replaces the custom assertions in elastic/docs-builder
(`tests/Elastic.Markdown.Tests/PrettyHtmlExtensions.cs`, `tests/Elastic.Authoring.Tests/Framework/HtmlAssertions.cs`,
about 200 call sites of `ShouldBeHtml` / `ConvertsToHtml`). Verify was rejected: v33+ adds a build-time sponsorship
check.

## Principles

- Lightweight, no opinions. Never auto-accept; CI never accepts.
- Failure messages are plain text (no ANSI), repo-relative paths, capped, readable by people and agents.
- Core has no heavy dependencies. Libraries are AOT/trim-clean except where a dependency (AngleSharp) cannot be.
- Property-based tests (CsCheck) are the main strategy for the diff engine.

## Packages

| Package | Contents |
|---|---|
| `Nullean.Snapshots` | store, modes, scrubbers, line diff (DiffPlex), Node tree, TreeDiffer, message formatter |
| `Nullean.Snapshots.Json` | System.Text.Json to Node |
| `Nullean.Snapshots.Yaml` | SharpYaml (>= 3.16) to Node, with line:col |
| `Nullean.Snapshots.Html` | AngleSharp + AngleSharp.Diffing |
| `Nullean.Snapshots.AwesomeAssertions` | `Should().MatchSnapshot()`, `BeHtml`, `BeJson`, `BeYaml` |
| `Nullean.Snapshots.TUnit` | test identity (class, method, args) |
| `Nullean.Snapshots.Cli` | tool `snap`, native AOT, file-level only |

`weichch/system-text-json-jsondiffpatch` is a test-only oracle, never a runtime dependency.

## Core design

- Files are UTF-8 without BOM, LF, one trailing newline. On pass a stale received file is deleted; on fail the
  received file is written, then the assertion throws.
- Modes: compare by default (a missing verified file fails as "new snapshot"); `SNAPSHOT_ACCEPT=1` writes verified
  locally; with `CI=true` the variable is ignored with a message.
- Duplicate keys within a run fail: "two tests resolve to X; pass a name".
- Scrubbers: built-in guid and ISO date-time scrubbers, custom regex or `Func<string,string>` per call and globally
  through `SnapshotDefaults`.
- Failure message template:

  ```
  {Format} did not match snapshot: {verified path}
  {N} difference(s):
  $.items[2].name   expected "kibana"   actual "Kibana"
  $.items[3]        missing in actual   (verified.yml:14:5)
  $.meta.extra      unexpected in actual
  (+3 more, see received)
  Received: {received path}
  Accept:   snap accept {key}   (or: dnx Nullean.Snapshots.Cli accept {key})
     or:    SNAPSHOT_ACCEPT=1 dotnet test --project {proj} --treenode-filter "{filter}"
  ```

  Text uses unified hunks (`@@ line N @@`, 3 context lines). HTML uses AngleSharp.Diffing labels plus the pretty
  printed line diff. Caps: 20 differences, 80 lines. Content is never a format string.

### Tree layer (phase 3)

`Node` records (`MapNode`, `SeqNode`, `ScalarNode` with Null/Bool/Int/Float/String kinds), JSONPath-like paths
(`$`, `.key`, `["odd key"]`, `[n]`, `$doc[n]` for YAML multi-doc). Maps are order-insensitive by default
(`MapOrder.Significant` reports `KeyOrderChanged`). Sequences align by LCS on deep equality bucketed by structural
hash, falling back to positional above a cell cap; similar unmatched delete/insert pairs recurse. Numbers compare
numerically unless `StrictNumbers`. Options: `IgnorePaths`, `NullEqualsMissing`. Deterministic output, behind
`ITreeDiffer`.

### Format packages

- **.Json:** parse via `JsonNode`, duplicate properties are an error, stored as 2-space pretty JSON; a passing compare
  never rewrites verified. `JsonSerializerOptions` and a `JsonTypeInfo<T>` overload for AOT.
- **.Yaml:** SharpYaml model API, Core schema (YAML 1.2), merge keys and anchors resolved. Stores the actual text
  verbatim. Messages carry `file:line:col` into the verified file.
- **.Html:** port docs-builder's pipeline (parse, optional CSS select, remove, remove attributes, pretty formatter,
  AngleSharp.Diffing). docs-builder specifics stay in docs-builder. `MatchSnapshot`, `BeHtml`, `ContainHtml`,
  `NotContainHtml`.

## CLI

`snap status [--json]` (exit 1 when anything is pending; `[{key, ext, state: changed|new, verified, received}]`),
`snap diff <key>`, `snap accept <key|glob|--all>`, `snap reject <key|glob|--all>`. Exit codes: 0 ok, 1 pending,
2 usage, 3 IO. Phase 5 adds interactive `snap` review and `snap orphans` with a manifest.

Packaging mirrors nullean/curb: `PackAsTool`, AOT for each of `linux-x64;linux-arm64;win-x64;win-arm64;osx-arm64`
built on a matching runner, plus a framework-dependent `any` fallback and the root pointer package. All packages share
the tag-derived version. Publishing is manual and gated; never unattended.

## CsCheck properties

Generators: random Node trees (depth <= 5, width <= 6, nasty strings, big ints, floats, -0) and an edit-script
generator applying k known, non-overlapping edits.

1. Reflexive. 2. Oracle agreement with JsonDiffPatch for JSON. 3. Emptiness symmetry and swap under `Diff(b,a)`.
4. Map edits give exactly k differences; sequence inserts give one Unexpected each; in general `|diffs| <= 2k` inside
mutated subtrees. 5. Shuffled keys give no diff unless `MapOrder.Significant`. 6. Swapping two distinct elements is a
diff. 7. Deterministic messages. 8. LCS matches a naive DP reference. 9. Normalize idempotent; parse(print(n)) = n.
10. JSON parsed by .Json and .Yaml gives equal trees. 11. YAML merge keys and anchors equal manual expansion, plus a
Core-schema typing table. 12. Scrubbers idempotent. 13. Message invariants: no ANSI, caps, Accept line, every path
resolves. 14. A 10k-node diff within budget; the cap fallback never throws.

1k iterations locally, 10k nightly, failing seeds kept as tests. The library's own failure messages are golden
snapshots in this repository.

## Phases

One PR each; done means CI green on linux, windows and macos.

0. **Scaffold**: solution, CPM, analyzers, editorconfig, CI, tag-based versioning. *(done, #1)*
1. **Core + text + .AwesomeAssertions + .TUnit + CLI** (status, diff, accept, reject) with the AOT pack and smoke
   pipeline. Prerelease `0.1.0-alpha.N` to a local feed only.
2. **.Html**, then migrate docs-builder: wrappers keep `PrettyHtmlExtensions` / `HtmlAssertions` method names, keep the
   H1 strip, `section#elastic-docs-v3` selector and `hx-*` removal in the wrappers, drop DiffPlex and
   AngleSharp.Diffing there, rework docs-builder #4654 to `MatchSnapshot` with `Select("main#content-container")`.
3. **Node tree + TreeDiffer + .Json** with the full CsCheck suite.
4. **.Yaml**, including cross-format properties and positions.
5. **snap review** (interactive), `snap orphans` and the manifest.
6. **Docs**: README, an AGENTS.md snippet ("accept: snap accept <key>; always read the git diff of verified files
   first"), a landing page on nullean.net. Then 1.0.

## Spikes

- TUnit `TestContext`: class, method, args, `--treenode-filter`. *(done: see DECISIONS.md)*
- AwesomeAssertions 9 custom assertions (`AssertionChain`). *(done)*
- SharpYaml 3.16 model API, merge keys, anchors, positions, AOT. *(phase 4)*
- AngleSharp and AngleSharp.Diffing under AOT/trim. *(phase 2)*
- curb's packaging and `dnx` behaviour. *(phase 1)*
