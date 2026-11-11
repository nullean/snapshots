# Nullean.Snapshots

![snapshots.](https://raw.githubusercontent.com/nullean/snapshots/main/docs/images/brand/png/lockup-light.png)

The core of Nullean.Snapshots, structural snapshot tests for .NET: the snapshot store, accept modes, scrubbers, the line and tree differs and the failure messages. Tests normally use it through `Nullean.Snapshots.AwesomeAssertions` or a format package; reference it directly to configure scrubbers or to build your own assertion.

[![NuGet](https://img.shields.io/nuget/v/Nullean.Snapshots?style=flat-square)](https://www.nuget.org/packages/Nullean.Snapshots)

## Install

```sh
dotnet add package Nullean.Snapshots
```

## Quick start

Scrub volatile values for every snapshot, once, before tests run, and build an assertion on the store:

```csharp
using System.Runtime.CompilerServices;
using Nullean.Snapshots;

internal static class SnapshotSetup
{
	[ModuleInitializer]
	internal static void Init()
	{
		SnapshotDefaults.AddScrubber(Scrubbers.Guids);     // Guid_1, Guid_2, ...
		SnapshotDefaults.AddScrubber(Scrubbers.DateTimes); // DateTime_1, ...
	}
}

public static class MyAssertions
{
	// Your own assertion on top of the store: the key comes from the call site.
	public static void MatchMySnapshot(
		this string actual,
		[CallerFilePath] string file = "",
		[CallerMemberName] string member = "",
		[CallerLineNumber] int line = 0)
	{
		var result = Snapshot.Match(actual, SnapshotFormats.Text, new SnapshotCallSite(file, member, line));
		if (!result.Passed)
			throw new InvalidOperationException(result.FailureMessage);
	}
}
```

## How it behaves

- A new snapshot fails: with no verified file the test fails and writes a received file. Accept it with
  `snap accept <key>` (the `Nullean.Snapshots.Cli` tool) after reading it.
- `SNAPSHOT_ACCEPT=1` accepts every mismatch in the run. With `CI` set to `1` or `true` it is ignored, and the
  failure message says so.
- Verified files: `.snapshots/<test file dir>/<Class>/<Method>[.<name>][.<argsHash>].verified.<ext>`; commit them.
  Received files go to `$(ArtifactsPath)/.snapshots/received/<Project>/...`, else `obj/snapshots/received/`.
- No scrubber runs by default. Global scrubbers run before per-call ones
  (`new SnapshotOptions().Scrub(regex, replacement)`).
- Text is stored as UTF-8 without BOM, LF endings and one trailing newline.

## Learn more

- Documentation: https://snapshots.nullean.net
- Layout of snapshot files: https://snapshots.nullean.net/reference/layout/
- Options: https://snapshots.nullean.net/reference/options/
- Source and issues: https://github.com/nullean/snapshots
