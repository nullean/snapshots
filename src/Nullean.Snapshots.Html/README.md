# Nullean.Snapshots.Html

![snapshots.](https://raw.githubusercontent.com/nullean/snapshots/main/docs/images/brand/png/lockup-light.png)

HTML snapshots and inline HTML assertions for Nullean.Snapshots: pretty-printed with AngleSharp and compared as a DOM, so whitespace and attribute order never fail a test.

[![NuGet](https://img.shields.io/nuget/v/Nullean.Snapshots.Html?style=flat-square)](https://www.nuget.org/packages/Nullean.Snapshots.Html)

## Install

```sh
dotnet add package Nullean.Snapshots.Html
```

## Quick start

```csharp
using AwesomeAssertions;
using Nullean.Snapshots.Html;

public class PageTests
{
	[Test]
	public void Renders()
	{
		var options = new HtmlSnapshotOptions()
			.Select("main#content")
			.Remove("script")
			.RemoveAttributes("hx-*", "nonce");
		Page.Render().Should().MatchHtmlSnapshot(options); // .verified.html, compared as a DOM
	}

	[Test]
	public void Greets() =>
		Page.Render().Should().ContainHtml("<p class=\"lead\">Welcome</p>");
}
```

- `MatchHtmlSnapshot` stores `.verified.html`; `MatchInlineHtmlSnapshot` keeps the expected markup in the test.
- `BeHtml`, `ContainHtml` and `NotContainHtml` compare with markup written in the test.
- `HtmlSnapshotOptions` narrows what is printed: select, remove elements, remove attributes, then transforms, in that
  order. It parses markup only, with no scripts or browser.

## Learn more

- Documentation: https://snapshots.nullean.net
- HTML snapshots: https://snapshots.nullean.net/formats/html/
- Source and issues: https://github.com/nullean/snapshots
