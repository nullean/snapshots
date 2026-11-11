# Nullean.Snapshots.Browser.Assertions

![snapshots.](https://raw.githubusercontent.com/nullean/snapshots/main/docs/images/brand/png/lockup-light.png)

AwesomeAssertions extensions for Nullean.Snapshots.Browser: run a journey in Chrome and assert its transcript (`JourneyTest`, `MatchJourneySnapshot`), or one page's accessibility tree (`MatchAriaSnapshot`).

[![NuGet](https://img.shields.io/nuget/v/Nullean.Snapshots.Browser.Assertions?style=flat-square)](https://www.nuget.org/packages/Nullean.Snapshots.Browser.Assertions)

## Install

```sh
dotnet add package Nullean.Snapshots.Browser.Assertions
```

## Quick start

A test case per journey file (`[JourneyFiles]` is in `Nullean.Snapshots.TUnit`), skipping the ones the site does not support, with the reason shown:

```csharp
using Nullean.Snapshots;
using Nullean.Snapshots.Browser;
using Nullean.Snapshots.TUnit;

public class JourneyTests
{
	private static readonly JourneySnapshotOptions Options = new() { BaseUrl = new Uri("http://localhost:5000/") };

	[Test]
	[JourneyFiles("journeys")]                                    // one test case per .journey file
	public async Task Run(SnapshotFile journey)
	{
		if (JourneyTest.SkipReason(journey, Options) is { } why)
			Skip.Test(why);
		await JourneyTest.RunAsync(journey, Options);
	}
}
```

Run and assert in two steps to pass `SnapshotOptions` or a reason:
`transcript.Should().MatchJourneySnapshot(because: "the basket shows the lamp")`. A Nullean.Cdp page's accessibility
tree is stored as `.verified.aria` with `(await page.SnapshotAsync(new() { Compact = true })).Should().MatchAriaSnapshot()`.

## Learn more

- Documentation: https://snapshots.nullean.net
- Browser journeys: https://snapshots.nullean.net/formats/browser/
- Source and issues: https://github.com/nullean/snapshots
