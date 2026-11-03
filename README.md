# Nullean.Snapshots

Snapshot testing for .NET on [AwesomeAssertions](https://awesomeassertions.org): plain text, HTML, JSON and YAML, with
a small native AOT CLI, `snap`, to review what changed and accept it.

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

- **Structural:** HTML compares as a DOM, JSON and YAML as trees; a changed field is reported by its path.
- **Plain, capped messages** that read the same in a terminal, a CI log and an agent's context.
- **Never accepts on its own**, and never in CI.
- **Lightweight:** trim- and native-AOT-safe packages, and every write scoped to the snapshot folders.

| Package | For |
|---|---|
| `Nullean.Snapshots.AwesomeAssertions` | `Should().MatchSnapshot()` for text |
| `Nullean.Snapshots.Html` | `MatchHtmlSnapshot`, `BeHtml`, `ContainHtml`, `NotContainHtml` |
| `Nullean.Snapshots.Json` | `MatchJsonSnapshot` for JSON text and objects, `BeJson` |
| `Nullean.Snapshots.Yaml` | `MatchYamlSnapshot`, `BeYaml` |
| `Nullean.Snapshots.TUnit` | the running test's class, method and arguments |
| `Nullean.Snapshots.Cli` | `snap`: review, accept, reject, find orphans |

Documentation: **[snapshots.nullean.net](https://snapshots.nullean.net)**.

Versions are 0.x prereleases while the API settles.

## Contributing

See [AGENTS.md](AGENTS.md) for conventions, [DECISIONS.md](DECISIONS.md) for the choices behind the design and
[ROADMAP.md](ROADMAP.md) for the plan. `scripts/docs.sh` builds and serves the documentation locally, given a
`docs-builder` binary.

## License

MIT, see [LICENSE.txt](LICENSE.txt).
