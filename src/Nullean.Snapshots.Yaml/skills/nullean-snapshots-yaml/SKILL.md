---
name: nullean-snapshots-yaml
description: >-
  Use when a .NET test snapshots or asserts YAML: generated config, Kubernetes or CI manifests, or an expected YAML
  literal. Nullean.Snapshots.Yaml adds AwesomeAssertions that store YAML as written and compare it as a tree, with
  differences pointing at line and column.
---

# Nullean.Snapshots.Yaml

YAML snapshots on top of `Nullean.Snapshots`: comments and layout are kept in the file, while the comparison only sees
the data. Use the text `MatchSnapshot` instead when formatting or comments are what you are testing.

## The calls you need

`using Nullean.Snapshots.Yaml;` and, for diff options, `using Nullean.Snapshots.Trees;`.

```csharp
manifest.Should().MatchYamlSnapshot();                 // .verified.yml
manifest.Should().MatchYamlSnapshot(name: "staging");  // a second snapshot in one test
```

```csharp
var diff = new TreeDiffOptions { IgnorePaths = ["$.metadata.resourceVersion"], MapOrder = MapOrder.Significant };
manifest.Should().MatchYamlSnapshot(diff);
```

Expected YAML in the test, compared as a tree:

```csharp
manifest.Should().BeYaml("items: [{id: 1}]");
manifest.Should().MatchInlineYamlSnapshot("""
    items:
      - id: 1
    """);
```

## Gotchas

- The extension is `.yml`, not `.yaml`: `<Method>.verified.yml`.
- The file is stored exactly as received, never re-emitted. Two inputs with the same data but different layout both
  pass against one verified file.
- Plain scalars use the YAML 1.2 core schema: `yes`, `no`, `on`, `off` are strings, `0x1F` equals `31`, and
  `1_000` is a string. Quote values whose type matters.
- Invalid YAML fails the assertion with "Expected valid YAML"; so does a repeated key in one mapping, an unknown
  alias or a non-scalar key.
- Anchors, aliases and `<<` merge keys are resolved before comparing.
- A multi-document stream pairs documents by position; paths read `$doc[1].name`.
- Paths in `IgnorePaths` use the same syntax as JSON: `$.items[*].etag`, `$..id`.

Docs: https://snapshots.nullean.net/formats/yaml
