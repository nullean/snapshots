using System.IO.Abstractions;
using AwesomeAssertions;
using Nullean.Cdp;
using Nullean.Snapshots.Cli;

namespace Nullean.Snapshots.Browser.Tests;

/// <summary>
/// Journeys in a real Chrome against the pages in site/. The runner images have Chrome; without one, or when it cannot
/// be started, these tests fail: they are never skipped.
/// </summary>
public class LiveJourneyTests
{
	private static string Project => JourneySnapshot.ProjectDirectory(ThisFile());

	private static string ThisFile([System.Runtime.CompilerServices.CallerFilePath] string path = "") => path;

	internal static Uri Site => new(Path.Combine(Project, "site") + Path.DirectorySeparatorChar);

	// One Chrome for the tests that need a page of their own, next to JourneySnapshot's shared one that runs journeys. A Chrome
	// per test (up to five at once, about 3 GB) is more than a 7 GB macOS runner survives alongside the other test projects.
	private static readonly Lazy<Task<Cdp.Browser>> PageBrowser = new(LaunchPageBrowserAsync, LazyThreadSafetyMode.ExecutionAndPublication);

	private static async Task<Cdp.Browser> LaunchPageBrowserAsync()
	{
		var browser = await Cdp.Browser.LaunchAsync();
		AppDomain.CurrentDomain.ProcessExit += (_, _) => browser.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(10));
		return browser;
	}

	internal static void RequireChrome()
	{
		var location = new ChromeLocator().Locate();
		if (!location.Found)
			throw new InvalidOperationException("The browser tests need Chrome. " + location.Describe());
	}

	public static IEnumerable<string> Journeys() => JourneySnapshot.Find();

	[Test]
	public void FindsTheProjectsJourneys() =>
		Journeys().Should().Equal("journeys/basket/empty.journey", "journeys/checks.journey", "journeys/shop.journey");

	[Test]
	public void AMoreSpecificFolderClaimsItsJourneys()
	{
		var assembly = typeof(ProjectJourneys).Assembly;

		var outer = new Nullean.Snapshots.TUnit.JourneyFilesAttribute("journeys");
		var basket = new Nullean.Snapshots.TUnit.JourneyFilesAttribute("journeys/basket");

		outer.Files(assembly, SnapshotFile.Find(assembly, outer.Folder, outer.Pattern)).Select(f => f.Name).Should().Equal("checks", "shop");
		basket.Files(assembly, SnapshotFile.Find(assembly, basket.Folder, basket.Pattern))
			.Should().Equal(new SnapshotFile("journeys/basket/empty.journey", "empty"));
	}

	[Test]
	public async Task SnapJourneyRunGivesAJourneyThatWritesFilesAFolder()
	{
		RequireChrome();
		var output = new StringWriter();
		var error = new StringWriter();
		var cli = new JourneyCli(new FileSystem(), Project, output, error,
			(file, options, ct) => JourneySnapshot.RunAsync(file, options, ct),
			_ => null);

		// Its own copy of checks.journey: a journey's artifacts folder belongs to one run at a time.
		var exit = await cli.RunAsync(["cli-journeys/checks.journey"], Site.AbsoluteUri, CancellationToken.None);

		error.ToString().Should().BeEmpty();
		output.ToString().Trim().Should().Be("ok   Nullean.Snapshots.Browser.Tests/cli-journeys/checks", "the screenshot step had a folder to write into");
		exit.Should().Be(0);
	}

	[Test]
	public async Task AllowedHostsReachTheJourney()
	{
		RequireChrome();
		var folder = Directory.CreateTempSubdirectory("journey-allow-");
		try
		{
			var journey = Path.Combine(folder.FullName, "away.journey");
			File.WriteAllText(journey, "open https://elsewhere.invalid/\n");

			var refused = await JourneySnapshot.RunAsync(journey, new JourneySnapshotOptions { BaseUrl = Site });
			var allowed = await JourneySnapshot.RunAsync(journey, new JourneySnapshotOptions { BaseUrl = Site, AllowedHosts = ["elsewhere.invalid"] });

			refused.Failure!.Message.Should().Contain("allow list");
			allowed.Failure!.Message.Should().NotContain("allow list", "the host is allowed, so the navigation is tried (and the name does not resolve)");
		}
		finally
		{
			folder.Delete(true);
		}
	}

	[Test]
	public async Task OptionsReachTheJourney()
	{
		RequireChrome();
		var artifacts = Directory.CreateTempSubdirectory("journey-artifacts-");
		try
		{
			var transcript = await JourneySnapshot.RunAsync("journeys/checks.journey",
				new JourneySnapshotOptions { BaseUrl = Site, ArtifactsDirectory = artifacts.FullName });

			transcript.Failure.Should().BeNull();
			File.Exists(Path.Combine(artifacts.FullName, "shots", "shop.png")).Should().BeTrue();
		}
		finally
		{
			artifacts.Delete(true);
		}
	}

	[Test]
	public async Task SnapJourneyRunComparesWithTheSameVerifiedFile()
	{
		RequireChrome();
		var output = new StringWriter();
		var error = new StringWriter();
		var cli = new JourneyCli(new FileSystem(), Project, output, error,
			(file, options, ct) => JourneySnapshot.RunAsync(file, options, ct),
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
	public async Task PassingAssertionsAreNotInTheTranscript()
	{
		RequireChrome();

		var transcript = await JourneySnapshot.RunAsync("journeys/shop.journey", new JourneySnapshotOptions { BaseUrl = Site });

		transcript.Failure.Should().BeNull();
		transcript.Text.Should().NotContain("expect", "a passing assertion records nothing, so the verified transcript is the one without it");
	}

	[Test]
	public async Task AFailingAssertionFailsTheStepWithTheAssertionsMessage()
	{
		RequireChrome();
		var file = Path.Combine(Path.GetTempPath(), $"expect-{Guid.NewGuid():N}.journey");
		await File.WriteAllTextAsync(file, "open index.html\nget title\nexpect --timeout 200 text #count 5\nget url\n");
		try
		{
			var transcript = await JourneySnapshot.RunAsync(file, new JourneySnapshotOptions { BaseUrl = Site });

			transcript.Text.Should().Be("## get title\nurl: {origin}/index.html\n> Fixture shop\n", "the reads before the assertion stay, and nothing after it runs");
			transcript.Failure!.Line.Should().Be(3);
			transcript.Failure.Command.Should().Be("expect --timeout 200 text #count 5");
			transcript.Failure.Message.Should().Be("Timed out after 0.2s waiting for css #count to have text \"5\": received \"0\".");
		}
		finally
		{
			File.Delete(file);
		}
	}

	[Test]
	public async Task SnapJourneyRunReportsAFailingAssertionAndExitsOne()
	{
		RequireChrome();
		var output = new StringWriter();
		var error = new StringWriter();
		var cli = new JourneyCli(new FileSystem(), Project, output, error,
			(file, options, ct) => JourneySnapshot.RunAsync(file, options, ct),
			_ => null);

		var exit = await cli.RunAsync(["cli-journeys/expect-fails.journey"], Site.AbsoluteUri, CancellationToken.None);

		exit.Should().Be(1);
		// The message names the journey by path, which differs per machine and per checkout.
		(output.ToString() + error.ToString()).Replace("\r\n", "\n", StringComparison.Ordinal)
			.Replace(Project.Replace('\\', '/').TrimEnd('/') + "/", "{project}/", StringComparison.Ordinal)
			.Should().MatchSnapshot();
	}

	[Test]
	public async Task AnAccessibilityTreeMatchesItsSnapshot()
	{
		RequireChrome();
		var browser = await PageBrowser.Value;
		await using var context = await browser.NewContextAsync();
		var page = await context.NewPageAsync();
		await page.GotoAsync(new Uri(Site, "basket.html").AbsoluteUri);

		(await page.SnapshotAsync(new() { Compact = true })).Should().MatchAriaSnapshot();
	}
}
