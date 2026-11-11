using System.IO.Abstractions;
using System.Text.RegularExpressions;
using AwesomeAssertions;
using Nullean.Cdp;
using Nullean.Snapshots.Cli;

namespace Nullean.Snapshots.Browser.Tests;

/// <summary>
/// Journeys in a real Chrome against the pages in site/. The runner images have Chrome; without one, or when it cannot
/// be started, these tests fail: they are never skipped.
/// </summary>
public partial class LiveJourneyTests
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

	// A run prints how long it took, which differs per machine and per run. On Windows a file URL has a drive letter after
	// three slashes where Linux has the root's first folder, which moves the {project} placeholder, and lines end with CRLF.
	private static string Normalise(string text) =>
		ElapsedSeconds().Replace(
			text.ReplaceLineEndings("\n").Replace("file:///{project}", "file://{project}", StringComparison.Ordinal),
			m => m.Value.StartsWith("in", StringComparison.Ordinal) ? "in <t>s" : "(<t>s)"
		);

	[GeneratedRegex(@"\(\d+\.\ds\)|in \d+\.\ds")]
	private static partial Regex ElapsedSeconds();

	[Test]
	public void FindsTheProjectsJourneys() =>
		Journeys().Should().Equal("journeys/basket/empty.journey", "journeys/checks.journey", "journeys/preview-only.journey", "journeys/shop.journey");

	[Test]
	public void AMoreSpecificFolderClaimsItsJourneys()
	{
		var assembly = typeof(ProjectJourneys).Assembly;

		var outer = new Nullean.Snapshots.TUnit.JourneyFilesAttribute("journeys");
		var basket = new Nullean.Snapshots.TUnit.JourneyFilesAttribute("journeys/basket");

		outer.Files(assembly, SnapshotFile.Find(assembly, outer.Folder, outer.Pattern)).Select(f => f.Name).Should().Equal("checks", "preview-only", "shop");
		basket
			.Files(assembly, SnapshotFile.Find(assembly, basket.Folder, basket.Pattern))
			.Should()
			.Equal(new SnapshotFile("journeys/basket/empty.journey", "empty"));
	}

	[Test]
	public async Task SnapJourneyRunGivesAJourneyThatWritesFilesAFolder()
	{
		RequireChrome();
		var output = new StringWriter();
		var error = new StringWriter();
		var cli = new JourneyCli(new FileSystem(), Project, output, error, (file, options, ct) => JourneySnapshot.RunAsync(file, options, ct), _ => null);

		// Its own copy of checks.journey: a journey's artifacts folder belongs to one run at a time.
		var exit = await cli.RunAsync(["cli-journeys/checks.journey"], Site.AbsoluteUri, CancellationToken.None);

		error.ToString().Should().BeEmpty();
		Normalise(output.ToString().Trim())
			.Should()
			.StartWith("ok      cli-journeys/checks (<t>s)\n", "the screenshot step had a folder to write into")
			.And
			.Contain("1 passed, 0 failed, 0 skipped");
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
			var transcript = await JourneySnapshot.RunAsync(
				"journeys/checks.journey",
				new JourneySnapshotOptions { BaseUrl = Site, ArtifactsDirectory = artifacts.FullName }
			);

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
		var cli = new JourneyCli(new FileSystem(), Project, output, error, (file, options, ct) => JourneySnapshot.RunAsync(file, options, ct), _ => null);

		var exit = await cli.RunAsync(["journeys/shop.journey"], Site.AbsoluteUri, CancellationToken.None);

		error.ToString().Should().BeEmpty();
		Normalise(output.ToString().Trim()).Should().StartWith("ok      journeys/shop (<t>s)\n").And.Contain("1 passed, 0 failed, 0 skipped");
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
			var transcript = await JourneySnapshot.RunAsync(
				file,
				new JourneySnapshotOptions { BaseUrl = Site, ActionTimeout = TimeSpan.FromMilliseconds(200) }
			);

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

			transcript
				.Text
				.Should()
				.Be("## get title\nurl: {origin}/index.html\n> Fixture shop\n", "the reads before the assertion stay, and nothing after it runs");
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
	public async Task SnapJourneyRunExplainsUsageTextPastedWithItsBrackets()
	{
		var error = new StringWriter();
		var cli = new JourneyCli(
			new FileSystem(),
			Project,
			new StringWriter(),
			error,
			(_, _, _) => throw new InvalidOperationException("nothing should run"),
			_ => null
		);

		var exit = await cli.RunAsync(
			["cli-journeys/checks.journey", "[--variant", "plain]", "[--transcript", "off]", "missing.journey"],
			null,
			CancellationToken.None
		);

		exit.Should().Be(2);
		error
			.ToString()
			.Replace("\r\n", "\n", StringComparison.Ordinal)
			.Should()
			.MatchInlineSnapshot(
				"""
			Error: no journey matches 'missing.journey'.
			Error: '[--variant', 'plain]', '[--transcript', 'off]' look like options from usage text, not journeys. Brackets there mark an optional part and are not typed: write --variant plain --transcript off.
			"""
			);
	}

	[Test]
	public async Task SnapJourneyRunReportsAFailingAssertionAndExitsOne()
	{
		RequireChrome();
		var output = new StringWriter();
		var error = new StringWriter();
		var cli = new JourneyCli(new FileSystem(), Project, output, error, (file, options, ct) => JourneySnapshot.RunAsync(file, options, ct), _ => null);

		var exit = await cli.RunAsync(["cli-journeys/expect-fails.journey"], Site.AbsoluteUri, CancellationToken.None);

		exit.Should().Be(1);
		// The message names the journey by path, which differs per machine and per checkout.
		Normalise(
			(output.ToString() + error.ToString()).Replace("\r\n", "\n", StringComparison.Ordinal).Replace(
				Project.Replace('\\', '/').TrimEnd('/') + "/",
				"{project}/",
				StringComparison.Ordinal
			)
		).Should().MatchSnapshot();
	}

	[Test]
	public async Task AFailingNetworkAssertionFailsTheStepAndKeepsTheReadsBeforeIt()
	{
		RequireChrome();
		await using var server = new TelemetryServer();

		var transcript = await JourneySnapshot.RunAsync("cli-journeys/telemetry-fails.journey", new JourneySnapshotOptions { BaseUrl = new Uri(server.Url) });

		transcript.Text.Should().Be("## get title\nurl: {origin}/\n> Docs\n", "the read before the assertion stays, and the one after it never runs");
		transcript.Failure!.Line.Should().Be(6);
		transcript.Failure.Command.Should().Be("expect --timeout 300 request **/_api/v1/o/l --body-contains no-such-text");
		transcript
			.Failure
			.Message
			.Should()
			.StartWith(
				"Timed out after 0.3s waiting for the requests to include a request matching \"**/_api/v1/o/l\" --body-contains \"no-such-text\": received 1 with that URL, none matching the other filters: POST "
			);
		transcript.Failure.Message.Should().NotContain("docs-frontend", "a failure shows what was asked for and what matched the URL, never the body");
	}

	[Test]
	public async Task SnapJourneyRunReportsAFailingNetworkAssertionAndExitsOne()
	{
		RequireChrome();
		await using var server = new TelemetryServer();
		var output = new StringWriter();
		var error = new StringWriter();
		var cli = new JourneyCli(new FileSystem(), Project, output, error, (file, options, ct) => JourneySnapshot.RunAsync(file, options, ct), _ => null);

		var exit = await cli.RunAsync(["cli-journeys/telemetry-fails.journey"], server.Url, CancellationToken.None);

		exit.Should().Be(1);
		// The message names the journey by path and the request by the server's port, which differ per machine and per run.
		Normalise(
			(output.ToString() + error.ToString())
				.Replace("\r\n", "\n", StringComparison.Ordinal)
				.Replace(Project.Replace('\\', '/').TrimEnd('/') + "/", "{project}/", StringComparison.Ordinal)
				.Replace(server.Url.TrimEnd('/'), "{origin}", StringComparison.Ordinal)
		).Should().MatchSnapshot();
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
