# YAML

```csharp
using Nullean.Snapshots.Yaml;

config.Should().MatchYamlSnapshot();
config.Should().BeYaml("items: [{id: 1}]");
```

YAML snapshots are stored **as written**: comments and layout survive, nothing is re-emitted. They are compared as
trees, the same way as [JSON](json.md), with differences pointing at line and column in the verified file:

```text
YAML did not match snapshot: tests/App.Tests/.snapshots/ConfigTests/Renders.verified.yml
3 difference(s):
$.items[0].name   expected "kibana"   actual "Kibana"   (verified.yml:3:11)
$.items[1]        missing in actual   (verified.yml:5:5)
$.meta.extra      unexpected in actual
```

## Typing

Plain scalars are typed with the **YAML 1.2 core schema**, not 1.1:

| Written | Is |
|---|---|
| `true`, `True`, `TRUE`, `false`, … | a boolean |
| `yes`, `no`, `on`, `off` | a string |
| `null`, `Null`, `NULL`, `~`, nothing | null |
| `42`, `+1`, `017` | a decimal integer |
| `0o17`, `0x1F` | an integer, equal to its decimal value |
| `1_000`, `0b101` | a string |
| `1.5`, `1e3`, `.inf`, `-.Inf`, `.nan` | a float |
| `"true"`, `'1'`, block scalars | a string |

`!!str`, `!!int`, `!!float`, `!!bool` and `!!null` tags apply; local tags such as `!ref` are typed as if they were not
there.

## Anchors and merge keys

Aliases are the anchored value, and `<<` merge keys are resolved: explicit keys win over merged ones, and of several
merged mappings the first wins. A YAML snapshot with anchors compares equal to the same data written out in full.

A stream of several documents compares document by document, with paths such as `$doc[1].name`.
