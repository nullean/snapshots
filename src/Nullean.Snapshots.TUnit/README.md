# Nullean.Snapshots.TUnit

![snapshots.](https://raw.githubusercontent.com/nullean/snapshots/main/docs/images/brand/png/lockup-light.png)

TUnit test identity for Nullean.Snapshots: snapshot names come from the running test's runtime class, method and arguments, so every case of a data-driven test gets its own file. Adds the `[SnapshotFiles]`, `[JourneyFiles]` and `[HttpFiles]` data sources.

[![NuGet](https://img.shields.io/nuget/v/Nullean.Snapshots.TUnit?style=flat-square)](https://www.nuget.org/packages/Nullean.Snapshots.TUnit)

## Install

```sh
dotnet add package Nullean.Snapshots.TUnit
```

## Quick start

Reference it from every TUnit test project that asserts snapshots; a `buildTransitive` targets file registers it, so there is nothing to call.

```csharp
using AwesomeAssertions;
using Nullean.Snapshots;
using Nullean.Snapshots.TUnit;

public class RenderTests
{
	[Test]
	[Arguments("kibana")]
	[Arguments("beats")]
	public void Renders(string product) =>
		Catalog.Greet(product).Should().MatchSnapshot();        // Renders.<argsHash>.verified.txt per case

	[Test]
	[SnapshotFiles("cases", "*.md")]                              // one case per file: Formats(nested/case)
	public void Formats(SnapshotFile file)
	{
		var result = Snapshot.MatchFile(File.ReadAllText(file.Path).ToUpperInvariant(), SnapshotFormats.Text, file.Path);
		if (!result.Passed)
			Assert.Fail(result.FailureMessage!);
	}
}
```

- Without this package the class folder is the test file name and arguments add no hash, so every case of a
  data-driven test shares one verified file.
- The hash is of each argument's invariant text. Use records or primitives: a class without a `ToString()` override
  hashes the same for every case and fails, asking for a name.
- The folder is the runtime class, so `[InheritsTests]` classes get their own snapshots.
- To register by hand, set `<SnapshotsTUnitAutoRegister>false</SnapshotsTUnitAutoRegister>` and call
  `TUnitTestIdentity.Register()` from a `[Before(Assembly)]` hook.

## Learn more

- Documentation: https://snapshots.nullean.net
- Layout of snapshot files: https://snapshots.nullean.net/reference/layout/
- Source and issues: https://github.com/nullean/snapshots
