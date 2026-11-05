<p align="center">
  <a href="https://snapshots.nullean.net">
    <picture>
      <source media="(prefers-color-scheme: light)" srcset="https://raw.githubusercontent.com/nullean/snapshots/main/docs/images/brand/png/lockup-light.png">
      <img src="https://raw.githubusercontent.com/nullean/snapshots/main/docs/images/brand/png/lockup.png" alt="snapshots." width="276" height="96">
    </picture>
  </a>
</p>

<p align="center"><strong>Structural Snapshot tests for .NET</strong></p>

<p align="center">
  <a href="https://www.nuget.org/packages/Nullean.Snapshots.AwesomeAssertions"><img alt="NuGet" src="https://img.shields.io/nuget/v/Nullean.Snapshots.AwesomeAssertions?color=9fc9ea&label=nuget"></a>
  <a href="https://github.com/nullean/snapshots/actions/workflows/ci.yml"><img alt="CI" src="https://github.com/nullean/snapshots/actions/workflows/ci.yml/badge.svg"></a>
  <a href="https://snapshots.nullean.net"><img alt="Docs" src="https://img.shields.io/badge/docs-snapshots.nullean.net-c6dd7a"></a>
</p>

---

Nullean.Snapshots is snapshot testing on [AwesomeAssertions](https://awesomeassertions.org) for text, HTML, JSON,
YAML and PNG images. A test's output is compared with a verified file kept with your tests; when it differs, the test fails with
a short, plain message that says exactly what moved and how to accept it. A small native AOT CLI, `snap`, reviews and
accepts what changed.

```csharp
[Test]
public void Serializes() =>
	catalog.Should().MatchJsonSnapshot(AppJsonContext.Default.Catalog);
```

```text
JSON did not match snapshot: tests/Shop.Tests/.snapshots/CatalogTests/Serializes.verified.json
3 difference(s):
$.items[0].name   expected "kibana"   actual "Kibana"   (verified.json:5:15)
$.items[1]        missing in actual   (verified.json:7:5)
$.extra           unexpected in actual
Received: .artifacts/.snapshots/received/Shop.Tests/CatalogTests/Serializes.received.json
Accept:   snap accept Shop.Tests/CatalogTests/Serializes   (or: dnx Nullean.Snapshots.Cli accept Shop.Tests/CatalogTests/Serializes)
```

## How it works

- **Compared by structure, not by text.** HTML compares as a DOM; JSON and YAML compare as trees. Attribute order,
  whitespace, key order and `1` versus `1.0` do not fail a test, and a changed field is reported by its path, with
  the line and column in the verified file. Images compare by their pixels, perceptually and with anti-aliasing left
  out, so a screenshot passes on every machine.
- **Verified files, or the value right in the test.** Short values can be [inline snapshots](https://snapshots.nullean.net/formats/inline/):
  `MatchInlineSnapshot("""…""")` keeps the expected value as a string literal, and accepting rewrites that literal.
- **Messages people and agents can act on.** Plain text without colour codes, repository-relative paths, at most 20
  differences and 80 lines, and the exact command to accept. They read the same in a terminal, a CI log and an
  agent's context.
- **You accept, or nothing changes.** Nothing is ever accepted for you. `SNAPSHOT_ACCEPT=1` works on your machine;
  with `CI` set it is ignored, and the failure says so.
- **Tidy.** Verified files live in one `.snapshots/` folder per test project, mirroring your test files. Received
  files go to `obj/` or your artifacts folder, never into source.
- **Lightweight.** Every package is trimming- and native-AOT-safe, the core has no heavy dependencies, and every
  write goes through a file system scoped to the snapshot folders.

## Packages

| Package | For |
|---|---|
| [`Nullean.Snapshots.AwesomeAssertions`](https://www.nuget.org/packages/Nullean.Snapshots.AwesomeAssertions) | `Should().MatchSnapshot()` and `MatchInlineSnapshot()` for text, and the base the format packages build on |
| [`Nullean.Snapshots.Html`](https://www.nuget.org/packages/Nullean.Snapshots.Html) | `MatchHtmlSnapshot`, `MatchInlineHtmlSnapshot`, `BeHtml`, `ContainHtml`, `NotContainHtml`; select, remove and transform first |
| [`Nullean.Snapshots.Json`](https://www.nuget.org/packages/Nullean.Snapshots.Json) | `MatchJsonSnapshot` and `MatchInlineJsonSnapshot` for JSON text and objects (AOT-safe through `JsonTypeInfo<T>`), `BeJson` |
| [`Nullean.Snapshots.Yaml`](https://www.nuget.org/packages/Nullean.Snapshots.Yaml) | `MatchYamlSnapshot`, `MatchInlineYamlSnapshot`, `BeYaml`; the YAML 1.2 core schema, anchors and merge keys, stored as written |
| [`Nullean.Snapshots.Images`](https://www.nuget.org/packages/Nullean.Snapshots.Images) | `MatchImageSnapshot` for PNG screenshots; compared by pixels, with a diff image on failure |
| `Nullean.Snapshots.Browser` | browser journeys: agent-browser commands run in Chrome with [Nullean.Cdp](https://cdp.nullean.net), each read compared as a tree (`JourneySnapshot`, `snap journey run`) |
| `Nullean.Snapshots.Browser.Assertions` | `MatchJourneySnapshot` and `MatchAriaSnapshot` for AwesomeAssertions |
| `Nullean.Snapshots.Http` | HTTP responses and `.http` files run with [Nullean.HttpFileParser](https://github.com/nullean/snapshots/tree/main/src/Nullean.HttpFileParser): status, chosen headers and JSON bodies compared as trees, secrets redacted (`HttpSnapshot`, `HttpFileSnapshot`, `snap http run`) |
| `Nullean.Snapshots.Http.Assertions` | `MatchHttpSnapshot`, `MatchHttpFileSnapshot` and `HttpFileTest` for AwesomeAssertions |
| [`Nullean.Snapshots.TUnit`](https://www.nuget.org/packages/Nullean.Snapshots.TUnit) | the running TUnit test's class, method and arguments, so every case gets its own file |
| [`Nullean.Snapshots.Cli`](https://www.nuget.org/packages/Nullean.Snapshots.Cli) | `snap`: review, diff, accept, reject, and find orphaned snapshots |

## Getting started

```xml
<PackageReference Include="Nullean.Snapshots.AwesomeAssertions" Version="0.*" />
<PackageReference Include="Nullean.Snapshots.Json" Version="0.*" />
<PackageReference Include="Nullean.Snapshots.TUnit" Version="0.*" />
```

```sh
dotnet tool install --global Nullean.Snapshots.Cli
snap            # walk through what changed: accept, reject or skip each snapshot
```

The first run of a new snapshot fails and writes a received file. Read it, accept it with `snap accept <key>` (or
`snap review`), and commit the verified file. From then on a change in output is a decision you make with the
difference in front of you.

Read more at **[snapshots.nullean.net](https://snapshots.nullean.net)**: the formats, the `snap` workflow,
snapshots in CI, working with coding agents, and the reference.

Versions are 0.x while the API settles. The packages need .NET 10.

## Contributing

See [AGENTS.md](AGENTS.md) for conventions, [DECISIONS.md](DECISIONS.md) for the choices behind the design and
[ROADMAP.md](ROADMAP.md) for the plan. `scripts/docs.sh` builds and serves the documentation locally, given a
`docs-builder` binary.

## License

MIT, see [LICENSE.txt](LICENSE.txt).
