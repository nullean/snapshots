using System.IO.Abstractions.TestingHelpers;
using AwesomeAssertions;
using Nullean.Cdp.Journeys;
using Nullean.Snapshots.Cli;
using Nullean.Snapshots.Tests;

namespace Nullean.Snapshots.Browser.Tests;

/// <summary>A journey run against two sites: the CLI with a fake browser, and Chrome against the pages in site/ and site-preview/.</summary>
public class JourneyCompareTests
{
	private const string ProdNav = "## snapshot -c -s nav\nurl: {origin}/index.html\n- navigation \"Main\"\n  - link \"Home\"\n  - link \"Basket\"\n";
	private const string PreviewNav = ProdNav + "  - link \"What's new\"\n";

	private sealed class Run
	{
		public TestProject P { get; } = new();
		public StringWriter Out { get; } = new();
		public StringWriter Err { get; } = new();
		public List<string> Ran { get; } = [];
		public Func<string, JourneyTranscript>? Prod { get; set; }
		public Func<string, JourneyTranscript>? Preview { get; set; }

		public Run()
		{
			P.Fs.AddFile(Journey, new MockFileData("open index.html\nsnapshot -c -s nav\nscreenshot nav.png\n"));
			P.Fs.AddDirectory(P.Project.ReceivedDirectory);
			Prod = file => new JourneyTranscript(file, ProdNav, null);
			Preview = file => new JourneyTranscript(file, PreviewNav, null);
		}

		public string Journey => P.Fs.Path.Combine(P.Project.Directory, "journeys", "shop.journey");

		public string Rules => P.Fs.Path.Combine(P.Project.Directory, "journeys", "shop.differences");

		/// <summary>The screenshots each site's run writes, by host: <c>nav.png</c> and its bytes.</summary>
		public Dictionary<string, (string Name, byte[] Png)[]> Pictures { get; } = [];

		public List<(string Host, string? Artifacts, bool ScreenshotOnFailure)> Runs { get; } = [];

		public Task<int> Compare(
			string? baseline = "http://prod.test/", string? target = "http://preview.test/", bool reuse = false, string? record = null, string? prune = null,
			params string[] patterns
		) => CompareWith(baseline, target, reuse, record, prune, "report", false, patterns);

		public Task<int> CompareWith(
			string? baseline, string? target, bool reuse, string? record, string? prune, string imageDiff, bool screenshotOnFailure, params string[] patterns
		) =>
			new JourneyCli(P.Fs, P.Project.Directory, Out, Err, (file, options, _) =>
			{
				Ran.Add(options.BaseUrl!.Host);
				Runs.Add((options.BaseUrl.Host, options.ArtifactsDirectory, options.ScreenshotOnFailure));
				if (options.ArtifactsDirectory is { } folder && Pictures.TryGetValue(options.BaseUrl.Host, out var pictures))
				{
					foreach (var (name, png) in pictures)
						P.Fs.AddFile(P.Fs.Path.Combine(folder, name), new MockFileData(png));
				}

				return Task.FromResult((options.BaseUrl.Host == "prod.test" ? Prod! : Preview!)(file));
			}, _ => null).CompareAsync(patterns.Length == 0 ? ["journeys/shop.journey"] : patterns, baseline, target, CancellationToken.None,
				reuseBaseline: reuse, record: record, prune: prune, more: new JourneyRunArgs { Interactive = false, ScreenshotOnFailure = screenshotOnFailure }, imageDiff: imageDiff);

		public string Output => Out.ToString().Replace("\r\n", "\n", StringComparison.Ordinal);

		public string Error => Err.ToString().Replace("\r\n", "\n", StringComparison.Ordinal);
	}

	[Test]
	public async Task AnUnexpectedDifferenceFailsAndSuggestsTheRule()
	{
		var run = new Run();

		(await run.Compare()).Should().Be(1);

		run.Output.Should().Contain("1 unexplained difference(s)").And.Contain("  ## snapshot -c -s nav\n  added link \"What's new\"")
			.And.Contain("0 passed, 1 failed, 0 skipped");
		run.Ran.Should().Equal("prod.test", "preview.test");
		run.P.Fs.AllFiles.Should().Contain(f => f.EndsWith("shop.baseline.journey", StringComparison.Ordinal))
			.And.Contain(f => f.EndsWith("shop.target.journey", StringComparison.Ordinal))
			.And.NotContain(f => f.Contains(".received.", StringComparison.Ordinal), "nothing is pending for snap status");
	}

	[Test]
	public async Task AnExpectedDifferencePasses()
	{
		var run = new Run();
		run.P.Fs.AddFile(run.Rules, new MockFileData("## snapshot*\nadded link \"What's new\"\n"));

		(await run.Compare()).Should().Be(0);

		run.Output.Should().Contain("ok      journeys/shop (").And.Contain("1 passed, 0 failed, 0 skipped");
	}

	[Test]
	public async Task ARuleThatMatchesNothingFails()
	{
		var run = new Run();
		run.P.Fs.AddFile(run.Rules, new MockFileData("## snapshot*\nadded link \"What's new\"\nremoved link \"Basket\"\n"));

		(await run.Compare()).Should().Be(1);

		run.Output.Should().Contain("1 rule(s) matched nothing, so they are stale:").And.Contain("shop.differences:3: removed link \"Basket\"");
	}

	[Test]
	public async Task ABaselineThatStopsAtAStepMeansNothingToCompareWith()
	{
		var run = new Run { Prod = file => new JourneyTranscript(file, ProdNav, new JourneyFailure(2, "snapshot -c -s nav", "no element matches.")) };

		(await run.Compare()).Should().Be(1);

		run.Output.Should().StartWith("The run against the baseline http://prod.test/ did not finish, so there is nothing to compare with:").And.Contain("no element matches.");
		run.Ran.Should().Equal("prod.test");
		run.P.Fs.AllFiles.Should().NotContain(f => f.Contains(".baseline.", StringComparison.Ordinal), "a partial baseline is never seeded");
	}

	[Test]
	public async Task AFailedStepOnTheTargetFailsTheComparison()
	{
		var run = new Run { Preview = file => new JourneyTranscript(file, ProdNav, new JourneyFailure(2, "snapshot -c -s nav", "boom")) };
		run.P.Fs.AddFile(run.Rules, new MockFileData("## snapshot*\nadded link x\n"));

		(await run.Compare()).Should().Be(1);

		run.Output.Should().Contain("boom");
	}

	[Test]
	public async Task ReusingTheBaselineRunsTheBaselineSiteOnlyForJourneysWithoutOne()
	{
		var run = new Run();
		await run.Compare();

		await run.Compare(reuse: true);

		run.Ran.Should().Equal("prod.test", "preview.test", "preview.test");
	}

	[Test]
	public async Task RecordFillsTheIncludedFileAndTheNextRunPasses()
	{
		var run = new Run();
		run.P.Fs.AddFile(run.Rules, new MockFileData("include? known/shop.differences\n"));
		var known = "journeys/known/shop.differences";

		(await run.Compare(record: known)).Should().Be(0);
		(await run.Compare(reuse: true)).Should().Be(0);

		run.Output.Should().Contain("Recorded 1 rule(s) for journeys/shop in journeys/known/shop.differences.");
		run.P.Read(run.P.Fs.Path.Combine(run.P.Project.Directory, "journeys", "known", "shop.differences")).Should()
			.Be("# recorded against http://preview.test/\n## snapshot -c -s nav\nadded link \"What's new\"\n");
	}

	[Test]
	public async Task PruneDropsTheRulesOfAChangeThatShipped()
	{
		var run = new Run { Preview = file => new JourneyTranscript(file, ProdNav, null) };
		run.P.Fs.AddFile(run.Rules, new MockFileData("include? known.differences\n"));
		run.P.Fs.AddFile(run.P.Fs.Path.Combine(run.P.Project.Directory, "journeys", "known.differences"), new MockFileData("## snapshot*\nadded link \"What's new\"\n"));

		(await run.Compare(prune: "journeys/known.differences")).Should().Be(0);

		run.P.Read(run.P.Fs.Path.Combine(run.P.Project.Directory, "journeys", "known.differences")).Should().BeEmpty();
	}

	[Test]
	public async Task BadInputExitsTwo()
	{
		var run = new Run();

		(await run.Compare(baseline: null)).Should().Be(2);
		(await run.Compare(target: "not a url")).Should().Be(2);
		(await run.Compare(patterns: "journeys/nope.journey")).Should().Be(2);
		run.P.Fs.AddFile(run.Rules, new MockFileData("nonsense\n"));
		(await run.Compare()).Should().Be(2);

		run.Error.Should().Contain("Error: --baseline is required").And.Contain("--target 'not a url' is not an absolute http, https or file URL")
			.And.Contain("no journey matches 'journeys/nope.journey'");
		run.Output.Should().Contain("'nonsense' is not a verb");
	}

	// Chrome, against two folders of pages: the preview has one more link in its navigation.

	private static Uri Preview => new(Path.Combine(LiveJourneyTests.Site.LocalPath, "..", "site-preview") + Path.DirectorySeparatorChar);

	private static Task<Differences.SiteComparisonResult> Real(string journey)
	{
		LiveJourneyTests.RequireChrome();
		return JourneySnapshot.CompareAsync($"compare-journeys/{journey}.journey", new JourneyCompareOptions { Baseline = LiveJourneyTests.Site, Target = Preview });
	}

	[Test]
	public async Task ThePreviewsExtraLinkIsExplainedByItsRule()
	{
		var result = await Real("nav");

		result.Passed.Should().BeTrue("{0}", result.FailureMessage);
		result.Application.Explained.Should().ContainSingle();
	}

	[Test]
	public async Task WithoutARuleTheExtraLinkFailsAndTheSuggestionIsTheRule()
	{
		var result = await Real("nav-norule");

		result.Passed.Should().BeFalse();
		result.FailureMessage.Should().Contain("added link \"What's new\"");
	}

	[Test]
	public async Task ARuleForSomethingThatDidNotChangeIsStale()
	{
		var result = await Real("nav-stale");

		result.Passed.Should().BeFalse();
		result.FailureMessage.Should().Contain("nav-stale.differences:3: removed link \"Basket\"");
	}
	// Screenshots: one folder per site, a diff image for each picture that differs

	private static byte[] Picture(bool dot)
	{
		var pixels = new byte[16 * 8 * 4];
		for (var i = 0; i < pixels.Length; i += 4)
		{
			pixels[i] = pixels[i + 1] = pixels[i + 2] = 200;
			pixels[i + 3] = 255;
		}

		if (dot)
		{
			pixels[(((3 * 16) + 5) * 4) + 1] = 0;
			pixels[(((3 * 16) + 5) * 4) + 2] = 0;
		}

		return Images.Png.Encode(new Images.RgbaImage(16, 8, pixels));
	}

	private static Run WithRule(Run run)
	{
		run.P.Fs.AddFile(run.Rules, new MockFileData("## snapshot*\nadded link \"What's new\"\n"));
		run.Pictures["prod.test"] = [("nav.png", Picture(false))];
		run.Pictures["preview.test"] = [("nav.png", Picture(true))];
		return run;
	}

	[Test]
	public async Task EachSiteGetsItsOwnArtifactsFolderSoTheSecondRunDoesNotEmptyTheFirst()
	{
		var run = WithRule(new Run());

		(await run.Compare()).Should().Be(0);

		run.Runs.Select(r => Path.GetFileName(r.Artifacts)).Should().Equal("shop.baseline.artifacts", "shop.target.artifacts");
		run.P.Fs.AllFiles.Should().Contain(f => f.EndsWith("shop.baseline.artifacts" + Path.DirectorySeparatorChar + "nav.png", StringComparison.Ordinal))
			.And.Contain(f => f.EndsWith("shop.target.artifacts" + Path.DirectorySeparatorChar + "nav.png", StringComparison.Ordinal));
	}

	[Test]
	public async Task ADifferingScreenshotIsReportedWithItsDiffImageAndIsNotAFailure()
	{
		var run = WithRule(new Run());

		(await run.Compare()).Should().Be(0);

		run.Output.Should().Contain("1 screenshot(s) differ between the baseline and the target (not a failure: --image-diff fail makes it one):")
			.And.Contain("journeys/shop  nav.png: 1 of 128 pixels differ")
			.And.Contain("x 5-5, y 3-3 (1x1): 1 pixel(s)")
			.And.Contain("shop.target.artifacts/nav.diff.png");
		run.P.Fs.AllFiles.Should().Contain(f => f.EndsWith("shop.target.artifacts" + Path.DirectorySeparatorChar + "nav.diff.png", StringComparison.Ordinal));
	}

	[Test]
	public async Task ImageDiffFailMakesADifferingScreenshotAFailure()
	{
		var run = WithRule(new Run());

		(await run.CompareWith("http://prod.test/", "http://preview.test/", false, null, null, "fail", false)).Should().Be(1);

		run.Output.Should().Contain("1 screenshot(s) differ: nav.png");
	}

	[Test]
	public async Task ImageDiffOffComparesNoPictures()
	{
		var run = WithRule(new Run());

		(await run.CompareWith("http://prod.test/", "http://preview.test/", false, null, null, "off", false)).Should().Be(0);

		run.Output.Should().NotContain("screenshot(s) differ");
		run.P.Fs.AllFiles.Should().NotContain(f => f.EndsWith(".diff.png", StringComparison.Ordinal));
	}

	[Test]
	public async Task IdenticalScreenshotsSayNothing()
	{
		var run = WithRule(new Run());
		run.Pictures["preview.test"] = [("nav.png", Picture(false))];

		(await run.Compare()).Should().Be(0);

		run.Output.Should().NotContain("screenshot(s) differ");
	}

	[Test]
	public async Task AReusedBaselineKeepsItsScreenshotsForTheNextComparison()
	{
		var run = WithRule(new Run());
		await run.Compare();

		(await run.Compare(reuse: true)).Should().Be(0);

		run.Ran.Should().Equal("prod.test", "preview.test", "preview.test");
		run.Output.Split("screenshot(s) differ between").Length.Should().Be(3, "the reused baseline's picture is still there to compare with, so both runs report it");
	}

	[Test]
	public async Task ScreenshotOnFailureReachesBothSites()
	{
		var run = WithRule(new Run());

		await run.CompareWith("http://prod.test/", "http://preview.test/", false, null, null, "report", true);

		run.Runs.Select(r => r.ScreenshotOnFailure).Should().Equal(true, true);
	}

	[Test]
	public async Task ABadImageDiffExitsTwo()
	{
		var run = new Run();

		(await run.CompareWith("http://prod.test/", "http://preview.test/", false, null, null, "loud", false)).Should().Be(2);

		run.Error.Should().Contain("--image-diff 'loud' is not 'report', 'fail' or 'off'");
		run.Ran.Should().BeEmpty();
	}

	// Chrome, with a screenshot step on pages that differ by one link

	[Test]
	public async Task TheScreenshotsOfTwoRealSitesDifferWhereThePreviewHasItsExtraLink()
	{
		var result = await Real("shot");

		result.Passed.Should().BeTrue("{0}", result.FailureMessage);
		var difference = JourneySnapshot.CompareScreenshots("compare-journeys/shot.journey").Should().ContainSingle().Subject;
		difference.Name.Should().Be("nav.png");
		difference.DiffPath.Should().NotBeNull();
		File.Exists(difference.DiffPath).Should().BeTrue();
	}
}
