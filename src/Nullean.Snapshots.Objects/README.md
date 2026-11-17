# Nullean.Snapshots.Objects

![snapshots.](https://raw.githubusercontent.com/nullean/snapshots/main/docs/images/brand/png/lockup-light.png)

Object-graph snapshots for Nullean.Snapshots: any object dumped by reflection as YAML, with cycles, unordered sets and throwing properties handled, so a property added to a type shows up as a diff by path.

[![NuGet](https://img.shields.io/nuget/v/Nullean.Snapshots.Objects?style=flat-square)](https://www.nuget.org/packages/Nullean.Snapshots.Objects)

## Install

```sh
dotnet add package Nullean.Snapshots.Objects
```

## Quick start

```csharp
using AwesomeAssertions;
using Nullean.Snapshots.Objects;

public class NavigationTests
{
	[Test]
	public void Builds() =>
		Navigation.Build(docset).Should().MatchObjectSnapshot();     // .verified.yml

	[Test]
	public void BuildsWithoutTheFileSystem() =>
		Navigation.Build(docset).Should().MatchObjectSnapshot(
			new ObjectDumpOptions().IgnoreType<IFileSystem>().IgnoreMember("Created").Render<Guid>(_ => "<guid>"));
}
```

Public instance properties and fields are written, maps by key, lists in order. A `HashSet<T>` is sorted by how its items render, so hash order does not make a diff. A reference back to an object on the path is `"<cycle to $.Path>"`. A property that throws is `"<throws InvalidOperationException>"`, without its message, which differs between operating systems.

## Trimming and native AOT

This package reads members by reflection, so the call is marked `[RequiresUnreferencedCode]`. It still works under native AOT if the trimmer is told which types to keep: the root type is kept for you, name every other type in the graph with `Include<T>()`. A type without public members that was not named fails with a message that says so, instead of silently dumping as `{}`. Interfaces are not consulted to find sets (trimming removes them from a type's interface list), so a custom set type needs `SortAsSet(typeof(MySet<>))`. See DECISIONS.md in the repository for the AOT run this was checked with.

Documentation: <https://snapshots.nullean.net/formats/objects/>
