---
name: nullean-snapshots-objects
description: >-
  Use when a .NET test should snapshot an object graph (a model, a navigation tree, a plan) without making it serialisable, so that a property
  added to a type fails the test as a diff by path. Nullean.Snapshots.Objects dumps public properties and fields by reflection as YAML.
---

# Nullean.Snapshots.Objects

## The call you need

`using Nullean.Snapshots.Objects;`

```csharp
model.Should().MatchObjectSnapshot();                                    // .verified.yml
model.Should().MatchObjectSnapshot(
	new ObjectDumpOptions()
		.IgnoreType<IFileSystem>()            // leave out members of a type, or deriving from it
		.IgnoreMember("CreatedAt")            // leave out a member by name, wherever it is
		.Render<Guid>(_ => "<guid>"));        // write a type as a string instead of walking it
string yaml = ObjectSnapshot.Dump(model);                                // just the text
```

Maps compare by key, lists keep their order, a `HashSet<T>` is sorted by how its items render. A cycle is `"<cycle to $.Path>"`, a throwing property is
`"<throws ExceptionType>"` with no message. Limits: `MaxDepth` (16) and `MaxItems` (1000).

## Gotchas

- It uses reflection: the call is `[RequiresUnreferencedCode]`. Under native AOT or trimming name every type in the graph with `.Include<T>()` (the root is
  included for you); a type with no public members that was not named throws, and the message says which.
- A set is recognised by type (`HashSet<>`, `ImmutableHashSet<>`, or `SortAsSet(typeof(MySet<>))`), never by its interfaces, so a normal and an AOT run give the same text.
- Only public instance properties and fields are read. Use `Render<T>` for a type whose state is private.
- For a type that is already serialisable and AOT-safe, `MatchJsonSnapshot` with a `JsonTypeInfo<T>` is the better fit.
