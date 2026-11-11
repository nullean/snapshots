---
name: nullean-snapshots-json
description: >-
  Use when a .NET test snapshots or asserts JSON: an API response body, a serialized object, or an expected JSON
  literal. Nullean.Snapshots.Json adds AwesomeAssertions that store JSON pretty-printed and compare it as a tree, with
  differences reported by path.
---

# Nullean.Snapshots.Json

JSON snapshots on top of `Nullean.Snapshots`: key order ignored, numbers compared by value, arrays aligned item by
item. Use the text `MatchSnapshot` instead when the exact bytes matter, such as whitespace or key order on the wire.

## The calls you need

`using Nullean.Snapshots.Json;` and, for diff options, `using Nullean.Snapshots.Trees;`.

```csharp
body.Should().MatchJsonSnapshot();                             // JSON text -> .verified.json
order.Should().MatchJsonSnapshot(AppJson.Default.Order);       // object, source-generated: AOT-safe
order.Should().MatchJsonSnapshot(JsonSerializerOptions.Web);   // object, reflection
```

Ignore volatile paths, or make order and number form count:

```csharp
var diff = new TreeDiffOptions { IgnorePaths = ["$.meta.generatedAt", "$..id"], StrictNumbers = true };
body.Should().MatchJsonSnapshot(diff);
```

Expected JSON in the test, compared as a tree:

```csharp
body.Should().BeJson("""{ "id": 2, "tags": ["a"] }""");
body.Should().MatchInlineJsonSnapshot("""
    { "id": 2, "name": "Kibana" }
    """);
```

## Gotchas

- `MatchJsonSnapshot` on invalid JSON text, or an object that repeats a property, throws `FormatException`
  instead of failing the assertion. `BeJson` fails the assertion.
- The object overload without options uses `JsonSerializerOptions.Default`: PascalCase names, no web defaults. Pass
  `JsonSerializerOptions.Web` to match what ASP.NET Core sends.
- That overload is marked `RequiresUnreferencedCode`; in trimmed or AOT test projects pass a `JsonTypeInfo<T>`.
- Scrubbers run on the pretty-printed text, and their output must stay valid JSON. Scrubbing a bare number to `<n>`
  gets accepted as an invalid file, and every later run fails with "The verified file is not valid JSON". Prefer
  `IgnorePaths` for values you do not care about.
- The verified file keeps the actual key order from when it was accepted; reordering keys later still passes
  unless `MapOrder = MapOrder.Significant`.
- `1` and `1.0` are equal by default. Arrays keep their order.
- A `null` string fails; a `null` object is snapshotted as `null`.

Docs: https://snapshots.nullean.net/formats/json
