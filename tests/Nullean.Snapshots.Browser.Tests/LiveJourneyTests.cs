using System.IO.Abstractions;
using AwesomeAssertions;
using Nullean.Cdp;
using Nullean.Snapshots.Cli;

namespace Nullean.Snapshots.Browser.Tests;

/// <summary>
/// Journeys in a real Chrome against the pages in site/. Without a Chrome they are skipped with the reason, unless
/// SNAPSHOTS_BROWSER_REQUIRED=1 (CI) makes that a failure.
/// </summary>
public class LiveJourneyTests
{
	private static string Project => JourneySnapshot.ProjectDirectory(ThisFile());

	private static string ThisFile([System.Runtime.CompilerServices.CallerFilePath] string path = "") => path;

	private static Uri Site => new(Path.Combine(Project, "site") + Path.DirectorySeparatorChar);

	private static void RequireChrome()
	{
		var location = new ChromeLocator().Locate();
		if (location.Found)
			return;
		if (Environment.GetEnvironmentVariable("SNAPSHOTS_BROWSER_REQUIRED") == "1")
			throw new InvalidOperationException("SNAPSHOTS_BROWSER_REQUIRED=1 but " + location.Describe());
		Skip.Test(location.Describe());
	}

	public static IEnumerable<string> Journeys() => JourneySnapshot.Find();

	[Test]
	[MethodDataSource(nameof(Journeys))]
	public async Task Runs(string journey)
	{
		RequireChrome();
		var transcript = await JourneySnapshot.RunAsync(journey, new JourneySnapshotOptions { BaseUrl = Site });

		transcript.Failure.Should().BeNull();
		transcript.Should().MatchJourneySnapshot();
	}

	[Test]
	public void FindsTheProjectsJourneys() => Journeys().Should().Equal("journeys/shop.journey");

	[Test]
	public async Task SnapJourneyRunComparesWithTheSameVerifiedFile()
	{
		RequireChrome();
		var output = new StringWriter();
		var error = new StringWriter();
		await using var browser = await Cdp.Browser.LaunchAsync();
		var cli = new JourneyCli(new FileSystem(), Project, output, error,
			(file, baseUri, ct) => JourneySnapshot.RunAsync(file, browser, new JourneySnapshotOptions { BaseUrl = baseUri }, ct),
			_ => null);

		var exit = await cli.RunAsync(["journeys/shop.journey"], Site.AbsoluteUri, CancellationToken.None);

		error.ToString().Should().BeEmpty();
		output.ToString().Trim().Should().Be("ok   Nullean.Snapshots.Browser.Tests/journeys/shop");
		exit.Should().Be(0);
	}

	[Test]
	public async Task AFailingStepFailsTheAssertionAndNamesTheStep()
	{
		RequireChrome();
		var file = Path.Combine(Path.GetTempPath(), $"broken-{Guid.NewGuid():N}.journey");
		await File.WriteAllTextAsync(file, "open index.html\nget title\nclick #missing\n");
		try
		{
			var transcript = await JourneySnapshot.RunAsync(file, new JourneySnapshotOptions { BaseUrl = Site, ActionTimeout = TimeSpan.FromMilliseconds(200) });

			transcript.Text.Should().Be("## get title\nurl: {origin}/index.html\n> Fixture shop\n");
			transcript.Failure!.Line.Should().Be(3);
			transcript.Failure.Message.Should().Be("Timed out after 0.2s waiting for css #missing to be visible and enabled: no element matches.");
		}
		finally
		{
			File.Delete(file);
		}
	}

	[Test]
	public async Task AnAccessibilityTreeMatchesItsSnapshot()
	{
		RequireChrome();
		await using var browser = await Cdp.Browser.LaunchAsync();
		await using var context = await browser.NewContextAsync();
		var page = await context.NewPageAsync();
		await page.GotoAsync(new Uri(Site, "basket.html").AbsoluteUri);

		(await page.SnapshotAsync(new() { Compact = true })).Should().MatchAriaSnapshot();
	}
}
