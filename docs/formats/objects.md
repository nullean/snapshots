# Objects

```csharp
using Nullean.Snapshots.Objects;

navigation.Should().MatchObjectSnapshot();
```

An object graph is dumped by reflection as YAML and compared as a [YAML tree](yaml.md), so a property added to a type shows up in the next run as an added path
(`$.Items[2].Added`), with nothing to write or keep in step. It needs no serialisable types: the case for it is a model with cycles, members System.Text.Json cannot
write (a `Type`, a file system) or unordered sets, where [JSON object snapshots](json.md#snapshotting-an-object-graph) stop. For a type that serialises well, prefer those.

What is written:

- Public instance **properties and fields**, in declaration order. Maps compare by key, so reordering is not a difference.
- **Lists and arrays** in order; a `HashSet<T>` sorted by how its items render, so hash order never makes a diff.
- **Dictionaries** by key. **Enums, dates, URIs, GUIDs and types** as strings, numbers as numbers.
- A reference back to an object on the current path as `"<cycle to $.Path>"`. A shared object that is not a cycle is written in full each time.
- A property that throws as `"<throws InvalidOperationException>"`, without the message, which differs between operating systems.

```csharp
navigation.Should().MatchObjectSnapshot(
    new ObjectDumpOptions()
        .IgnoreType<IFileSystem>()                // members declared as, or deriving from, a type
        .IgnoreMember("CreatedAt")                // by name, wherever it appears
        .Render<Guid>(_ => "<guid>"));            // a type written as a string you choose
```

`MaxDepth` (16) and `MaxItems` (1000) bound a large graph, and `ObjectSnapshot.Dump(value)` gives just the text. The `diff`, `name` and `options` arguments are the ones
of the [YAML](yaml.md) assertion, so scrubbers and `IgnorePaths` work the same.

## Trimming and native AOT

The call is marked `[RequiresUnreferencedCode]`: members are read by reflection. Under native AOT it works when the trimmer is told which types to keep. The root
type is kept for you; name every other type in the graph:

```csharp
new ObjectDumpOptions().Include<Inner>().Include<Leaf>()
```

A type with no public members that was not named fails with a message that says to name it, instead of silently dumping as `{}`. Sets are recognised by type, not by their
interfaces (trimming removes those), so a custom set type needs `SortAsSet(typeof(MySet<>))`. With every type named, a native AOT build gives the same text as a normal run.
