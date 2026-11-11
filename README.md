<p align="center">
  <a href="https://snapshots.nullean.net">
    <picture>
      <source media="(prefers-color-scheme: light)" srcset="docs/images/brand/png/lockup-light.png">
      <img src="docs/images/brand/png/lockup.png" alt="snapshots." width="276" height="96">
    </picture>
  </a>
</p>

<p align="center">Structural snapshot tests for .NET: text, HTML, JSON, YAML, images, browser journeys and HTTP, compared by structure and reported in plain messages people and agents can act on.</p>

<p align="center">
  <a href="https://www.nuget.org/packages/Nullean.Snapshots.AwesomeAssertions"><img src="https://img.shields.io/nuget/v/Nullean.Snapshots.AwesomeAssertions?style=flat-square" alt="NuGet"></a>
  <a href="https://github.com/nullean/snapshots/actions/workflows/ci.yml"><img src="https://img.shields.io/github/actions/workflow/status/nullean/snapshots/ci.yml?branch=main&style=flat-square&label=ci" alt="CI"></a>
  <a href="https://snapshots.nullean.net"><img src="https://img.shields.io/badge/docs-snapshots.nullean.net-20a0a0?style=flat-square" alt="Docs"></a>
</p>

![An image snapshot's diff: the verified screenshot faded to grey, the changed heading in red, outlined in blue](docs/images/image-diff.png)

<sub>A real diff image from `Nullean.Snapshots.Images`: the snapshots.nullean.net landing page, screenshotted, then
again with one word of its heading changed. Everything else on the page matched.</sub>

Nullean.Snapshots is snapshot testing on [AwesomeAssertions](https://awesomeassertions.org). A test's output is
compared with a verified file kept with your tests, or with a string literal right in the test (inline snapshots);
when it differs, the test fails with a short, plain message that says exactly what moved and how to accept it. A
small native AOT CLI, `snap`, reviews and accepts what changed.

```text
JSON did not match snapshot: tests/Shop.Tests/.snapshots/CatalogTests/Serializes.verified.json
3 difference(s):
$.items[0].name   expected "kibana"   actual "Kibana"   (verified.json:5:15)
$.items[1]        missing in actual   (verified.json:7:5)
$.extra           unexpected in actual
Received: .artifacts/.snapshots/received/Shop.Tests/CatalogTests/Serializes.received.json
Accept:   snap accept Shop.Tests/CatalogTests/Serializes   (or: dnx Nullean.Snapshots.Cli accept Shop.Tests/CatalogTests/Serializes)
```

## Install

```sh
dotnet add package Nullean.Snapshots.AwesomeAssertions
dotnet add package Nullean.Snapshots.Json
dotnet add package Nullean.Snapshots.TUnit
dotnet tool install --global Nullean.Snapshots.Cli
```

Add the format packages you need (`.Html`, `.Yaml`, `.Images.Assertions`, `.Browser.Assertions`, `.Http.Assertions`);
`Nullean.Snapshots.TUnit` gives every case of a data-driven TUnit test its own file. The packages need .NET 10.

## Quick start

```csharp
using AwesomeAssertions;
using Nullean.Snapshots;
using Nullean.Snapshots.Json;

public class CatalogTests
{
	[Test]
	public void Renders() =>
		Catalog.Render().Should().MatchSnapshot();          // .snapshots/CatalogTests/Renders.verified.txt

	[Test]
	public void Serializes() =>
		Catalog.ToJson().Should().MatchJsonSnapshot();      // compared as a tree, not as text

	[Test]
	public void Greets() =>
		Catalog.Greet("Kibana").Should().MatchInlineSnapshot("Hello, Kibana!");
}
```

The first run of a new snapshot fails and writes a received file. Read it, accept it, and commit the verified file:

```sh
snap            # walk through what changed: accept, reject or skip each snapshot
snap accept CatalogTests/Renders
```

From then on a change in output is a decision you make with the difference in front of you.

- **Compared by structure, not by text.** HTML compares as a DOM; JSON and YAML compare as trees. Attribute order,
  whitespace, key order and `1` versus `1.0` do not fail a test, and a changed field is reported by its path, with
  the line and column in the verified file. Images compare by their pixels, perceptually and with anti-aliasing left
  out, so a screenshot passes on every machine.
- **Verified files, or the value right in the test.** Short values can be [inline snapshots](https://snapshots.nullean.net/formats/inline/):
  `MatchInlineSnapshot("""…""")` keeps the expected value as a string literal, and accepting rewrites that literal.
- **Messages people and agents can act on.** Plain text without colour codes, repository-relative paths, at most 20
  differences and 80 lines, and the exact command to accept. They read the same in a terminal, a CI log and an
  agent's context.
- **Two sites compared.** `snap journey compare` and `snap http compare` run a journey or `.http` file against a baseline site and
  a preview and fail only on differences nobody expected, which are rules in a checked-in `.differences` file next to it; screenshots
  are compared too, with a diff image. See [expected differences](https://snapshots.nullean.net/workflow/expected-differences/).
- **You accept, or nothing changes.** Nothing is ever accepted for you. `SNAPSHOT_ACCEPT=1` works on your machine;
  with `CI` set it is ignored, and the failure says so.
- **Tidy.** Verified files live in one `.snapshots/` folder per test project, mirroring your test files. Received
  files go to `obj/` or your artifacts folder, never into source.
- **Lightweight.** Every package is trimming- and native-AOT-safe, the core has no heavy dependencies, and every
  write goes through a file system scoped to the snapshot folders.

## Packages

| Package | What it is | |
|---|---|---|
| [`Nullean.Snapshots`](src/Nullean.Snapshots/README.md) | the core: the snapshot store, accept modes, scrubbers, the line and tree differs and the failure messages; the other packages build on it | [![NuGet](https://img.shields.io/nuget/v/Nullean.Snapshots?style=flat-square)](https://www.nuget.org/packages/Nullean.Snapshots) |
| [`Nullean.Snapshots.AwesomeAssertions`](src/Nullean.Snapshots.AwesomeAssertions/README.md) | `Should().MatchSnapshot()` and `MatchInlineSnapshot()` for text, and the base the format packages build on | [![NuGet](https://img.shields.io/nuget/v/Nullean.Snapshots.AwesomeAssertions?style=flat-square)](https://www.nuget.org/packages/Nullean.Snapshots.AwesomeAssertions) |
| [`Nullean.Snapshots.Html`](src/Nullean.Snapshots.Html/README.md) | `MatchHtmlSnapshot`, `MatchInlineHtmlSnapshot`, `BeHtml`, `ContainHtml`, `NotContainHtml`; select, remove and transform first | [![NuGet](https://img.shields.io/nuget/v/Nullean.Snapshots.Html?style=flat-square)](https://www.nuget.org/packages/Nullean.Snapshots.Html) |
| [`Nullean.Snapshots.Json`](src/Nullean.Snapshots.Json/README.md) | `MatchJsonSnapshot` and `MatchInlineJsonSnapshot` for JSON text and objects (AOT-safe through `JsonTypeInfo<T>`), `BeJson` | [![NuGet](https://img.shields.io/nuget/v/Nullean.Snapshots.Json?style=flat-square)](https://www.nuget.org/packages/Nullean.Snapshots.Json) |
| [`Nullean.Snapshots.Yaml`](src/Nullean.Snapshots.Yaml/README.md) | `MatchYamlSnapshot`, `MatchInlineYamlSnapshot`, `BeYaml`; the YAML 1.2 core schema, anchors and merge keys, stored as written | [![NuGet](https://img.shields.io/nuget/v/Nullean.Snapshots.Yaml?style=flat-square)](https://www.nuget.org/packages/Nullean.Snapshots.Yaml) |
| [`Nullean.Snapshots.Images`](src/Nullean.Snapshots.Images/README.md) | PNG screenshots compared by pixels, with a diff image on failure (`ImageSnapshotFormat`) | [![NuGet](https://img.shields.io/nuget/v/Nullean.Snapshots.Images?style=flat-square)](https://www.nuget.org/packages/Nullean.Snapshots.Images) |
| [`Nullean.Snapshots.Images.Assertions`](src/Nullean.Snapshots.Images.Assertions/README.md) | `MatchImageSnapshot` for AwesomeAssertions | [![NuGet](https://img.shields.io/nuget/v/Nullean.Snapshots.Images.Assertions?style=flat-square)](https://www.nuget.org/packages/Nullean.Snapshots.Images.Assertions) |
| [`Nullean.Snapshots.Browser`](src/Nullean.Snapshots.Browser/README.md) | browser journeys: agent-browser commands run in Chrome with [Nullean.Cdp](https://cdp.nullean.net), each read compared as a tree (`JourneySnapshot`, `snap journey run`) | [![NuGet](https://img.shields.io/nuget/v/Nullean.Snapshots.Browser?style=flat-square)](https://www.nuget.org/packages/Nullean.Snapshots.Browser) |
| [`Nullean.Snapshots.Browser.Assertions`](src/Nullean.Snapshots.Browser.Assertions/README.md) | `MatchJourneySnapshot`, `MatchAriaSnapshot` and `JourneyTest` for AwesomeAssertions | [![NuGet](https://img.shields.io/nuget/v/Nullean.Snapshots.Browser.Assertions?style=flat-square)](https://www.nuget.org/packages/Nullean.Snapshots.Browser.Assertions) |
| [`Nullean.Snapshots.Browser.Console`](src/Nullean.Snapshots.Browser.Console/README.md) | runs a suite of journeys from the console with streamed results, grouped skips and readable failures, for `snap journey run` and any tool that wants the same output | [![NuGet](https://img.shields.io/nuget/v/Nullean.Snapshots.Browser.Console?style=flat-square)](https://www.nuget.org/packages/Nullean.Snapshots.Browser.Console) |
| [`Nullean.Snapshots.Http`](src/Nullean.Snapshots.Http/README.md) | HTTP responses and `.http` files run with `Nullean.HttpFileParser`: status, chosen headers and JSON bodies compared as trees, secrets redacted (`HttpSnapshot`, `HttpFileSnapshot`, `snap http run`) | [![NuGet](https://img.shields.io/nuget/v/Nullean.Snapshots.Http?style=flat-square)](https://www.nuget.org/packages/Nullean.Snapshots.Http) |
| [`Nullean.Snapshots.Http.Assertions`](src/Nullean.Snapshots.Http.Assertions/README.md) | `MatchHttpSnapshot`, `MatchHttpFileSnapshot` and `HttpFileTest` for AwesomeAssertions | [![NuGet](https://img.shields.io/nuget/v/Nullean.Snapshots.Http.Assertions?style=flat-square)](https://www.nuget.org/packages/Nullean.Snapshots.Http.Assertions) |
| [`Nullean.HttpFileParser`](src/Nullean.HttpFileParser/README.md) | parses, lints and runs `.http` files on its own, with no dependency on the snapshot packages; the portable subset of the Visual Studio, REST Client and JetBrains dialects | [![NuGet](https://img.shields.io/nuget/v/Nullean.HttpFileParser?style=flat-square)](https://www.nuget.org/packages/Nullean.HttpFileParser) |
| [`Nullean.Snapshots.TUnit`](src/Nullean.Snapshots.TUnit/README.md) | the running TUnit test's class, method and arguments, so every case gets its own file; `[SnapshotFiles]`, `[JourneyFiles]` and `[HttpFiles]` data sources | [![NuGet](https://img.shields.io/nuget/v/Nullean.Snapshots.TUnit?style=flat-square)](https://www.nuget.org/packages/Nullean.Snapshots.TUnit) |
| [`Nullean.Snapshots.Cli`](src/Nullean.Snapshots.Cli/README.md) | `snap`: review, diff, accept, reject, and find orphaned snapshots; accepting an inline snapshot rewrites the literal in the test; `snap journey run` and `snap http run` check journeys and `.http` files without a test run | [![NuGet](https://img.shields.io/nuget/v/Nullean.Snapshots.Cli?style=flat-square)](https://www.nuget.org/packages/Nullean.Snapshots.Cli) |

Versions are 0.x while the API settles.

## Learn more

- Documentation: https://snapshots.nullean.net (the formats, the `snap` workflow, snapshots in CI, working with
  coding agents, and the reference)
- [Releases](https://github.com/nullean/snapshots/releases)
- [Contributing](https://github.com/nullean/.github/blob/main/CONTRIBUTING.md); in this repository,
  [AGENTS.md](AGENTS.md) for conventions, [DECISIONS.md](DECISIONS.md) for the choices behind the design and
  [ROADMAP.md](ROADMAP.md) for the plan. `mise run docs` builds and serves the documentation locally.

MIT licensed. Copyright Nullean and contributors.
