# Nullean.Snapshots.Browser

![snapshots.](https://raw.githubusercontent.com/nullean/snapshots/main/docs/images/brand/png/lockup-light.png)

Browser journeys for Nullean.Snapshots: agent-browser style journeys run in Chrome with Nullean.Cdp, their transcripts, accessibility trees and reads compared as trees.

[![NuGet](https://img.shields.io/nuget/v/Nullean.Snapshots.Browser?style=flat-square)](https://www.nuget.org/packages/Nullean.Snapshots.Browser)

## Install

```sh
dotnet add package Nullean.Snapshots.Browser
```

## Quick start

A journey is one command per line; reads (`snapshot`, `get`, `is`) become transcript sections:

```text
open index.html
find role button click --name "Add red lamp"
snapshot -c -s "#results"
get text #count
```

Run it and compare, without a test framework:

```csharp
using Nullean.Snapshots.Browser;

public static class ShopJourney
{
	public static async Task CheckAsync()
	{
		var options = new JourneySnapshotOptions { BaseUrl = new Uri("http://localhost:5000/") };
		var transcript = await JourneySnapshot.RunAsync("journeys/shop.journey", options);
		var result = JourneySnapshot.Match(transcript);            // .snapshots/journeys/shop.verified.journey
		if (!result.Passed)
			throw new InvalidOperationException(result.FailureMessage);
	}
}
```

- In AwesomeAssertions tests use `Nullean.Snapshots.Browser.Assertions` (`JourneyTest.RunAsync`); for a suite in a
  console tool, `Nullean.Snapshots.Browser.Console`; from the command line, `snap journey run`.
- A live site: `Transcript = TranscriptMode.Off` and `ReadOnly = true` compare nothing and send no writes, and
  `JourneySnapshot.SkipReason` says when a journey does not apply.
- Two sites: `JourneySnapshot.CompareAsync` runs a journey against a baseline and a target and fails only on
  differences no rule in its `.differences` file expects. `screenshot` steps are compared too, with a diff image.

## Learn more

- Documentation: https://snapshots.nullean.net
- Browser journeys: https://snapshots.nullean.net/formats/browser/
- Expected differences: https://snapshots.nullean.net/workflow/expected-differences/
- Source and issues: https://github.com/nullean/snapshots
