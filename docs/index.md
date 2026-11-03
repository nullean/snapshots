# Nullean.Snapshots

Snapshot testing for .NET on [AwesomeAssertions](https://awesomeassertions.org): plain text, HTML, JSON and YAML,
with a small native AOT CLI, `snap`, to review what changed and accept it.

```csharp
[Test]
public void RendersTheCatalog() =>
	Render(catalog).Should().MatchHtmlSnapshot();
```

The first run fails and writes a *received* file. Look at it, accept it with `snap accept`, and commit the *verified*
file next to your tests. From then on the test compares against it, and a failure says exactly what moved:

```text
JSON did not match snapshot: tests/Shop.Tests/.snapshots/CatalogTests/Serializes.verified.json
3 difference(s):
$.items[0].name   expected "kibana"   actual "Kibana"   (verified.json:5:15)
$.items[1]        missing in actual   (verified.json:7:5)
$.extra           unexpected in actual
Received: .artifacts/.snapshots/received/Shop.Tests/CatalogTests/Serializes.received.json
Accept:   snap accept Shop.Tests/CatalogTests/Serializes   (or: dnx Nullean.Snapshots.Cli accept Shop.Tests/CatalogTests/Serializes)
   or:    SNAPSHOT_ACCEPT=1 dotnet test --project tests/Shop.Tests --treenode-filter "/Shop.Tests/Shop.Tests/CatalogTests/Serializes"
```

## What it is for

- **Plain, capped messages.** No colour codes, repository-relative paths, at most 20 differences and 80 lines. They
  read the same in a terminal, a CI log and an agent's context.
- **Structural comparison.** HTML compares as a DOM, JSON and YAML as trees: key order, whitespace and `1` versus
  `1.0` do not fail a test; a changed field is reported by its path.
- **Never accepts on its own.** Nothing is accepted unless you say so, and nothing is ever accepted in CI.
- **Lightweight.** The core has no heavy dependencies, and every package is trimming- and native-AOT-safe.

## Packages

| Package | For |
|---|---|
| `Nullean.Snapshots.AwesomeAssertions` | `Should().MatchSnapshot()` for text, and the base the format packages build on |
| `Nullean.Snapshots.Html` | `MatchHtmlSnapshot`, `BeHtml`, `ContainHtml`, `NotContainHtml` |
| `Nullean.Snapshots.Json` | `MatchJsonSnapshot` for JSON text and objects, `BeJson` |
| `Nullean.Snapshots.Yaml` | `MatchYamlSnapshot`, `BeYaml` |
| `Nullean.Snapshots.TUnit` | the running TUnit test's class, method and arguments, so every case gets its own file |
| `Nullean.Snapshots.Cli` | `snap`: review, accept, reject and find orphaned snapshots |

Start with [getting started](getting-started/index.md).
