# Nullean.Snapshots.Browser.Console

![snapshots.](https://raw.githubusercontent.com/nullean/snapshots/main/docs/images/brand/png/lockup-light.png)

Runs a suite of Nullean.Snapshots browser journeys from the console with streamed results, grouped skips and readable failures, for `snap journey run` and for any tool that wants the same output.

[![NuGet](https://img.shields.io/nuget/v/Nullean.Snapshots.Browser.Console?style=flat-square)](https://www.nuget.org/packages/Nullean.Snapshots.Browser.Console)

## Install

```sh
dotnet add package Nullean.Snapshots.Browser.Console
```

## Quick start

Run every journey under a folder against a site:

```csharp
using Nullean.Snapshots.Browser.Console;

public static class SmokeTool
{
	public static async Task<int> RunAsync(string root)
	{
		var options = new JourneyRunOptions
		{
			Files = JourneyRunFile.FromPaths(root, Directory.EnumerateFiles(root, "*.journey", SearchOption.AllDirectories)),
			BaseUrl = new Uri("https://shop.test/"),
			ReadOnly = true,
			Rerun = "smoke-tool run --only {id}",
		};
		return await JourneyConsoleRunner.RunAsync(options, Console.Out, Console.Error); // 0 when every journey passed
	}
}
```

- `Compare = (file, transcript) => JourneySnapshot.Match(transcript)` compares each transcript with its verified
  file too; left out, only steps and `expect` lines count.
- `Features` and `Tags` select journeys by their flags and tags.
- For one journey in a test, use `Nullean.Snapshots.Browser.Assertions`; to just run a suite, `snap journey run`
  already does this.

## Learn more

- Documentation: https://snapshots.nullean.net
- Browser journeys: https://snapshots.nullean.net/formats/browser/
- Source and issues: https://github.com/nullean/snapshots
