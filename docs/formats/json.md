# JSON

```csharp
using Nullean.Snapshots.Json;

responseBody.Should().MatchJsonSnapshot();                                     // JSON text
product.Should().MatchJsonSnapshot(AppJsonContext.Default.Product);            // an object, AOT-safe
product.Should().MatchJsonSnapshot(new JsonSerializerOptions(JsonSerializerDefaults.Web));
responseBody.Should().BeJson("""{ "id": 1, "tags": ["a"] }""");
```

JSON is stored indented by two spaces, properties in the order they were written, numbers as written and non-ASCII
text unescaped. It is compared as a tree:

- **Maps** compare by key; order does not matter unless you ask for it.
- **Numbers** compare by value: `1`, `1.0` and `10e-1` are equal, and integers beyond 2^53 still compare exactly.
- **Arrays** keep their order. Items are aligned by their longest common subsequence, so inserting one item reports
  that item, not everything after it, and a changed field inside an item is reported by its path:

```text
$[0]                     unexpected in actual
$[1].v (actual $[2].v)   expected "b"   actual "B"   (verified.json:1:31)
```

A property that appears twice in one object is an error, and so is invalid JSON.

## Snapshotting an object graph

`MatchJsonSnapshot` on an object serialises it with System.Text.Json and compares the JSON, so a property added to a record shows up in the
next run as an added path, with nothing else to write. Two things come from System.Text.Json itself, not from the snapshot:

- A **cycle** throws (`A possible object cycle was detected`). Set `ReferenceHandler = ReferenceHandler.IgnoreCycles` in the serializer options and
  the repeated reference is written as `null`.
- A member System.Text.Json cannot serialise, such as a `System.Type`, throws (`Serialization … of 'System.Type' instances is not supported`).
  Mark it `[JsonIgnore]`, or give the options a converter for it.

The overload that takes serializer options uses reflection, so it is not trim or native AOT safe; pass a generated `JsonTypeInfo<T>` where that matters.

## Options

```csharp
var diff = new TreeDiffOptions
{
	MapOrder = MapOrder.Significant,                       // report a different key order
	StrictNumbers = true,                                  // 1 and 1.0 differ
	NullEqualsMissing = true,                              // "x": null equals no "x"
	IgnorePaths = ["$.meta.generatedAt", "$..id", "$.items[*].etag"],
};
json.Should().MatchJsonSnapshot(diff);
```

See [paths and options](../reference/options.md).

The overload that takes `JsonSerializerOptions` serializes with reflection and is marked as such; use the
`JsonTypeInfo<T>` overload with a source-generated context in trimmed or native AOT test projects.

For a short value there is `MatchInlineJsonSnapshot`, which keeps the expected value in the test as a literal: see [inline snapshots](inline.md).
