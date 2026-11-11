# Nullean.Snapshots.AwesomeAssertions

![snapshots.](https://raw.githubusercontent.com/nullean/snapshots/main/docs/images/brand/png/lockup-light.png)

Text snapshot assertions for AwesomeAssertions, `value.Should().MatchSnapshot()` and `MatchInlineSnapshot()`, and the building blocks the HTML, JSON and YAML packages use. Part of Nullean.Snapshots, structural snapshot tests for .NET.

[![NuGet](https://img.shields.io/nuget/v/Nullean.Snapshots.AwesomeAssertions?style=flat-square)](https://www.nuget.org/packages/Nullean.Snapshots.AwesomeAssertions)

## Install

```sh
dotnet add package Nullean.Snapshots.AwesomeAssertions
```

## Quick start

The extensions live in the `Nullean.Snapshots` namespace:

```csharp
using AwesomeAssertions;
using Nullean.Snapshots;

public class ReportTests
{
	[Test]
	public void Renders()
	{
		var report = Report.Render();
		report.Should().MatchSnapshot();                     // .verified.txt, compared line by line
		Report.Header().Should().MatchSnapshot(name: "header"); // a second snapshot in the same test
	}

	[Test]
	public void Totals() =>
		Report.Totals().Should().MatchInlineSnapshot("""
			2 x beats
			1 x kibana
			""");
}
```

Leave out the inline snapshot's expected value for a new one; `snap accept` writes it into the test.

For structured output prefer `Nullean.Snapshots.Json` or `Nullean.Snapshots.Yaml`, which compare as trees, not
lines. Add `Nullean.Snapshots.TUnit` so every case of a data-driven test gets its own file.

## Learn more

- Documentation: https://snapshots.nullean.net
- Text snapshots: https://snapshots.nullean.net/formats/text/
- Inline snapshots: https://snapshots.nullean.net/formats/inline/
- Source and issues: https://github.com/nullean/snapshots
