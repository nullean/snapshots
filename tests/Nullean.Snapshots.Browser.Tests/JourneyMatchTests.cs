using System.IO.Abstractions.TestingHelpers;
using AwesomeAssertions;
using Nullean.Cdp.Journeys;
using Nullean.Snapshots.Cli;
using Nullean.Snapshots.Tests;

namespace Nullean.Snapshots.Browser.Tests;

/// <summary>Matching a transcript, and 'snap journey run', on a mock file system with a fake browser.</summary>
public class JourneyMatchTests
{
	private const string Good = "## get title\nurl: {origin}/index.html\n> Shop\n";
	private const string Changed = "## get title\nurl: {origin}/index.html\n> Store\n";

	private static string Journey(TestProject p) => p.Fs.Path.Combine(p.Project.Directory, "journeys", "shop.journey");

	[Test]
	public async Task AJourneyRejectsTheShapeMode()
	{
		var run = () => JourneySnapshot.RunAsync("shop.journey", null!, new JourneySnapshotOptions { Transcript = TranscriptMode.Shape });

		(await run.Should().ThrowAsync<ArgumentException>()).Which.Message.Should().Contain("applies to .http runs");
	}

	[Test]
	public void AFailedStepFailsEvenWhenThePartialTranscriptMatches()
	{
		var p = new TestProject();
		p.Fs.AddFile(p.VerifiedFile("journeys/shop", "journey"), new MockFileData(Good));
		var transcript = new JourneyTranscript(
			Journey(p),
			Good,
			new JourneyFailure(4, "click #pay", "Timed out after 5s waiting for css #pay to be visible and enabled: no element matches.")
		);

		var result = JourneySnapshot.WithFailure(transcript, p.Engine.MatchFile(transcript.Text, JourneySnapshot.Format, transcript.File, null, null));

		result.Passed.Should().BeFalse();
		result
			.FailureMessage
			.Should()
			.Be(
				$"Journey stopped at {Relative(transcript.File)}:4: click #pay\nTimed out after 5s waiting for css #pay to be visible and enabled: no element matches."
			);
	}

	[Test]
	public void AFailedStepComesBeforeTheDifference()
	{
		var p = new TestProject();
		p.Fs.AddFile(p.VerifiedFile("journeys/shop", "journey"), new MockFileData(Good));
		var transcript = new JourneyTranscript(Journey(p), Changed, new JourneyFailure(4, "click #pay", "boom"));

		var result = JourneySnapshot.WithFailure(transcript, p.Engine.MatchFile(transcript.Text, JourneySnapshot.Format, transcript.File, null, null));

		result.FailureMessage!.Replace(Relative(transcript.File), "shop.journey", StringComparison.Ordinal).Should().MatchSnapshot();
		p.Read(p.ReceivedFile("journeys/shop", "journey")).Should().Be(Changed, "the partial transcript is written for review");
	}

	[Test]
	public void AVariantIsComparedWithItsOwnVerifiedFile()
	{
		var p = new TestProject();
		p.Fs.AddFile(p.VerifiedFile("journeys/shop.preview", "journey"), new MockFileData(Good));
		var changed = new JourneyTranscript(Journey(p), Changed, null) { Variant = "preview" };

		var plain = p.Engine.MatchFile(Good, JourneySnapshot.Format, Journey(p), null, null);
		var preview = p.Engine.MatchFile(Good, JourneySnapshot.Format, Journey(p), null, null, "preview");
		var previewChanged = p.Engine.MatchFile(changed.Text, JourneySnapshot.Format, changed.File, null, null, changed.Variant);

		plain.Passed.Should().BeFalse("only the variant has a verified file");
		preview.Passed.Should().BeTrue();
		preview.Key.Should().Be("Acme.Tests/journeys/shop.preview");
		previewChanged.Passed.Should().BeFalse();
		p.Read(p.ReceivedFile("journeys/shop.preview", "journey")).Should().Be(Changed);
	}

	[Test]
	public void MatchUsesTheTranscriptsVariant()
	{
		var p = new TestProject();
		p.Fs.AddFile(p.VerifiedFile("journeys/shop.preview", "journey"), new MockFileData(Good));
		Snapshot.EngineOverride.Value = p.Engine;
		try
		{
			var preview = JourneySnapshot.Match(new JourneyTranscript(Journey(p), Good, null) { Variant = "preview" });
			var plain = JourneySnapshot.Match(new JourneyTranscript(Journey(p), Good, null));

			preview.Passed.Should().BeTrue();
			plain.Passed.Should().BeFalse("the plain file has no verified snapshot");
		}
		finally
		{
			Snapshot.EngineOverride.Value = null;
		}
	}

	[Test]
	public void ARunManifestNamesTheVariantFileSoItIsNeverAnOrphan()
	{
		var p = new TestProject();
		p.Fs.AddFile(p.VerifiedFile("journeys/shop.preview", "journey"), new MockFileData(Good));

		_ = p.Engine.MatchFile(Good, JourneySnapshot.Format, Journey(p), null, null, "preview");
		p.Engine.FlushManifests();

		var manifest = p.Fs.AllFiles.Single(f => f.EndsWith(RunManifest.FileName, StringComparison.Ordinal));
		p.Read(manifest).Should().Contain("journeys/shop.preview.verified.journey").And.NotContain("journeys/shop.verified.journey");
	}

	[Test]
	[Arguments("")]
	[Arguments(".preview")]
	[Arguments("pre view")]
	[Arguments("a/b")]
	public void AnInvalidVariantIsRejected(string variant)
	{
		var p = new TestProject();

		var act = () => p.Engine.MatchFile(Good, JourneySnapshot.Format, Journey(p), null, null, variant);

		act.Should().Throw<ArgumentException>().WithMessage("The variant '*' must be a non-empty name*");
	}

	[Test]
	public void WithTheTranscriptOffNoVerifiedFileIsNeeded()
	{
		var p = new TestProject();
		var transcript = new JourneyTranscript(Journey(p), Changed, null) { Mode = TranscriptMode.Off };

		var result = JourneySnapshot.Match(transcript);

		result.Passed.Should().BeTrue();
		p.Fs.AllFiles.Should().NotContain(f => f.Contains(".snapshots", StringComparison.Ordinal) || f.Contains("received", StringComparison.Ordinal));
	}

	[Test]
	public void WithTheTranscriptOffAFailedStepStillFails()
	{
		var p = new TestProject();
		var failure = new JourneyFailure(3, "expect visible #pay", "Timed out after 5s waiting for css #pay to be visible: no element matches.");
		var transcript = new JourneyTranscript(Journey(p), Good, failure) { Mode = TranscriptMode.Off };

		var result = JourneySnapshot.Match(transcript);

		result.Passed.Should().BeFalse();
		result
			.FailureMessage
			.Should()
			.Be(
				$"Journey stopped at {Relative(transcript.File)}:3: expect visible #pay\nTimed out after 5s waiting for css #pay to be visible: no element matches."
			);
	}

	[Test]
	public async Task TheVariantFlagComparesWithTheVariantFile()
	{
		var run = new Run();
		run.P.Fs.AddFile(run.P.VerifiedFile("journeys/shop.preview", "journey"), new MockFileData(Good));

		(await run.InvokeWith(null, "preview", null, "journeys/shop.journey")).Should().Be(0);

		run.Output.Should().StartWith("ok      journeys/shop (").And.Contain("1 passed, 0 failed, 0 skipped");
		run.Ran.Should().ContainSingle();
	}

	[Test]
	public async Task TheTranscriptFlagOffNeedsNoVerifiedFileAndStillReportsAFailedStep()
	{
		var run = new Run();
		run.Transcript =
			file => file.EndsWith("basket.journey", StringComparison.Ordinal)
				? new JourneyTranscript(file, Good, new JourneyFailure(2, "expect visible #pay", "no element matches."))
				: new JourneyTranscript(file, Changed, null);

		(await run.InvokeWith(null, null, "off", "journeys/**/*.journey")).Should().Be(1);

		run.Output.Should().Contain("Journey stopped at ").And.Contain("expect visible #pay");
		run.Output.Should().Contain("ok      journeys/shop (");
		run.P.Fs.AllFiles.Should().NotContain(f => f.Contains("received", StringComparison.Ordinal));
	}

	[Test]
	public async Task ABadTranscriptOrVariantFlagExitsTwo()
	{
		var run = new Run();

		(await run.InvokeWith(null, null, "maybe", "journeys/shop.journey")).Should().Be(2);
		(await run.InvokeWith(null, ".bad", null, "journeys/shop.journey")).Should().Be(2);

		run
			.Err
			.ToString()
			.Replace("\r\n", "\n", StringComparison.Ordinal)
			.Should()
			.Be(
				"Error: --transcript 'maybe' is not 'compare' or 'off'.\n" +
					"Error: --variant '.bad' must be a name of letters, digits, '.', '-' and '_', without a leading or trailing dot.\n"
			);
		run.Ran.Should().BeEmpty();
	}

	private static string Relative(string file)
	{
		var relative = Path.GetRelativePath(Directory.GetCurrentDirectory(), file);
		return (relative.StartsWith("..", StringComparison.Ordinal) ? file : relative).Replace('\\', '/');
	}

	// 'snap journey run' over the same mock project.
	private sealed class Run
	{
		public TestProject P { get; } = new();
		public StringWriter Out { get; } = new();
		public StringWriter Err { get; } = new();
		public Dictionary<string, string?> Env { get; } = [];
		public List<(string File, Uri? Base, JourneySnapshotOptions Options)> Ran { get; } = [];
		public Func<string, JourneyTranscript>? Transcript { get; set; }

		public Run()
		{
			P.Fs.AddFile(P.Fs.Path.Combine(P.Project.Directory, "journeys", "shop.journey"), new MockFileData("open index.html\nget title\n"));
			P.Fs.AddFile(P.Fs.Path.Combine(P.Project.Directory, "journeys", "nested", "basket.journey"), new MockFileData("open b.html\nget title\n"));
			P.Fs.AddFile(P.Fs.Path.Combine(P.Project.Directory, "bin", "copy.journey"), new MockFileData("get title\n"));
			// A verified transcript ends in .journey too; globs must not pick it up.
			P.Fs.AddFile(P.VerifiedFile("journeys/other", "journey"), new MockFileData(Good));
			Transcript = file => new JourneyTranscript(file, Good, null);
			// The project's tests have run before, so its received folder under the artifacts path exists.
			P.Fs.AddDirectory(P.Project.ReceivedDirectory);
		}

		public Task<int> Invoke(string? baseUrl, params string[] patterns) => InvokeWith(baseUrl, null, null, patterns);

		public Task<int> InvokeWith(string? baseUrl, string? variant, string? transcript, params string[] patterns) =>
			InvokeMore(new JourneyRunArgs { Interactive = false }, baseUrl, variant, transcript, patterns);

		public Task<int> InvokeMore(JourneyRunArgs more, string? baseUrl, string? variant, string? transcript, params string[] patterns) =>
			new JourneyCli(
				P.Fs,
				P.Project.Directory,
				Out,
				Err,
				(file, options, _) =>
				{
					Ran.Add((file, options.BaseUrl, options));
					return Task.FromResult(Transcript!(file));
				},
				k => Env.GetValueOrDefault(k)
			).RunAsync(patterns, baseUrl, CancellationToken.None, variant: variant, transcript: transcript, more: more with { Interactive = false });

		public string Output => Out.ToString().Replace("\r\n", "\n", StringComparison.Ordinal);
	}

	[Test]
	public async Task MatchingJourneysPrintOkAndExitZero()
	{
		var run = new Run();
		run.P.Fs.AddFile(run.P.VerifiedFile("journeys/shop", "journey"), new MockFileData(Good));
		run.P.Fs.AddFile(run.P.VerifiedFile("journeys/nested/basket", "journey"), new MockFileData(Good));

		(await run.Invoke("http://localhost:5000/", "journeys/**/*.journey")).Should().Be(0);

		run.Output.Should().Contain("ok      journeys/nested/basket (").And.Contain("ok      journeys/shop (").And.Contain("2 passed, 0 failed, 0 skipped");
		run.Ran.Should().AllSatisfy(r => r.Base.Should().Be(new Uri("http://localhost:5000/")));
		run.Ran.Select(r => r.File).Should().NotContain(f => f.Contains("bin", StringComparison.Ordinal) || f.Contains(".snapshots", StringComparison.Ordinal));
	}

	[Test]
	public async Task AGlobFromTheRootSkipsVerifiedTranscriptsAndBuildOutput()
	{
		var run = new Run();

		await run.Invoke(null, "**/*.journey");

		run
			.Ran
			.Select(r => run.P.Fs.Path.GetRelativePath(run.P.Project.Directory, r.File).Replace('\\', '/'))
			.Should()
			.Equal("journeys/nested/basket.journey", "journeys/shop.journey");
	}

	[Test]
	public async Task ADifferenceWritesReceivedAndExitsOneWithoutAManifest()
	{
		var run = new Run();
		run.P.Fs.AddFile(run.P.VerifiedFile("journeys/shop", "journey"), new MockFileData(Good));
		run.Transcript = file => new JourneyTranscript(file, Changed, null);

		(await run.Invoke(null, "journeys/shop.journey")).Should().Be(1);

		run
			.Output
			.Should()
			.Contain("FAILED  journeys/shop")
			.And
			.Contain("Journey did not match snapshot: tests/Acme.Tests/.snapshots/journeys/shop.verified.journey\n");
		run.Output.Should().Contain("Accept:   snap accept Acme.Tests/journeys/shop");
		run.P.Read(run.P.ReceivedFile("journeys/shop", "journey")).Should().Be(Changed);
		run.P.Fs.File.Exists(run.P.Fs.Path.Combine(run.P.Project.ReceivedDirectory, RunManifest.FileName)).Should().BeFalse();
	}

	[Test]
	public async Task WithoutAnArtifactsReceivedFolderReceivedFilesGoUnderObj()
	{
		var run = new Run();
		run.P.Fs.Directory.Delete(run.P.Project.ReceivedDirectory, recursive: true);

		(await run.Invoke(null, "journeys/shop.journey")).Should().Be(1);

		run
			.P
			.Fs
			.File
			.Exists(run.P.Fs.Path.Combine(run.P.Project.Directory, "obj", "snapshots", "received", "journeys", "shop.received.journey"))
			.Should()
			.BeTrue();
	}

	[Test]
	public async Task NeverAccepts()
	{
		var run = new Run();
		run.Env["SNAPSHOT_ACCEPT"] = "1";

		(await run.Invoke(null, "journeys/shop.journey")).Should().Be(1);

		run.P.Fs.File.Exists(run.P.VerifiedFile("journeys/shop", "journey")).Should().BeFalse();
		run.Err.ToString().Should().StartWith("SNAPSHOT_ACCEPT is ignored: 'snap journey run' never accepts.");
	}

	[Test]
	public async Task UsageErrorsExitTwo()
	{
		var run = new Run();
		(await run.Invoke(null)).Should().Be(2);
		(await run.Invoke("localhost:5000", "journeys/shop.journey")).Should().Be(2);
		(await run.Invoke(null, "journeys/missing.journey", "journeys/*.nope")).Should().Be(2);

		run
			.Err
			.ToString()
			.Replace("\r\n", "\n", StringComparison.Ordinal)
			.Should()
			.Be(
				"Error: name the journeys to run: files or globs such as 'journeys/**/*.journey'.\n" +
					"Error: --base-url 'localhost:5000' is not an absolute http, https or file URL.\n" +
					"Error: no journey matches 'journeys/missing.journey'.\n" + "Error: no journey matches 'journeys/*.nope'.\n"
			);
		run.Ran.Should().BeEmpty();
	}

	[Test]
	public async Task AJourneyErrorExitsTwoAndTheOthersStillRun()
	{
		var run = new Run();
		run.P.Fs.AddFile(run.P.VerifiedFile("journeys/shop", "journey"), new MockFileData(Good));
		run.Transcript =
			file => file.EndsWith("basket.journey", StringComparison.Ordinal)
				? throw new JourneyParseException("basket.journey", [new JourneyError(1, "'screenshot' is not supported in journeys.")])
				: new JourneyTranscript(file, Good, null);

		(await run.Invoke(null, "journeys/**/*.journey")).Should().Be(2);

		run.Output.Should().Contain("basket.journey:1: 'screenshot' is not supported in journeys.").And.Contain("FAILED  journeys/nested/basket");
		run.Output.Should().Contain("ok      journeys/shop (");
	}

	[Test]
	public async Task TheRunFlagsReachTheJourneyOptions()
	{
		var run = new Run();
		var more = new JourneyRunArgs
		{
			Features = ["preview"],
			Env = "demo",
			BuildType = "assembler",
			ReadOnly = true,
			Viewport = "1280x800",
			UserAgent = "Monitor/1",
			Headers = ["X-Monitor: true"],
			Writable = ["**/v1/o/l"],
		};

		(await run.InvokeMore(more, "http://localhost:5000/", null, "off", "journeys/shop.journey")).Should().Be(0);

		var options = run.Ran.Should().ContainSingle().Subject.Options;
		options.Features.Should().Equal("preview");
		(options.Env, options.BuildType, options.ReadOnly, options.UserAgent).Should().Be(("demo", "assembler", true, "Monitor/1"));
		options.Viewport.Should().Be(new JourneyViewport(1280, 800));
		options.Headers.Should().Contain("X-Monitor", "true");
		options.WritableUrls.Should().Equal("**/v1/o/l");
	}

	[Test]
	public async Task ARequiresLineThatTheSiteDoesNotMeetSkipsTheJourneyWithItsReason()
	{
		var run = new Run();
		run
			.P
			.Fs
			.AddFile(run.P.Fs.Path.Combine(run.P.Project.Directory, "journeys", "preview.journey"), new MockFileData("# @requires preview\nopen index.html\n"));

		var without = await run.InvokeMore(new JourneyRunArgs(), null, null, "off", "journeys/preview.journey", "journeys/shop.journey");
		var skipped = run.Output;
		var withFlag = await run.InvokeMore(new JourneyRunArgs { Features = ["preview"] }, null, null, "off", "journeys/preview.journey");

		without.Should().Be(0);
		skipped.Should().Contain("skipped journeys/preview (requires preview)").And.Contain("ok      journeys/shop (");
		withFlag.Should().Be(0);
		run.Ran.Select(r => Path.GetFileName(r.File)).Should().Equal("shop.journey", "preview.journey");
	}

	[Test]
	public async Task ABadViewportOrHeaderExitsTwo()
	{
		var run = new Run();

		(await run.InvokeMore(new JourneyRunArgs { Viewport = "wide" }, null, null, null, "journeys/shop.journey")).Should().Be(2);
		(await run.InvokeMore(new JourneyRunArgs { Headers = ["no colon"] }, null, null, null, "journeys/shop.journey")).Should().Be(2);

		run
			.Err
			.ToString()
			.Replace("\r\n", "\n", StringComparison.Ordinal)
			.Should()
			.Be(
				"Error: --viewport 'wide' is not a viewport: use --viewport <width>x<height>, e.g. 1280x800.\n" +
					"Error: --header 'no colon' is not a header: use --header \"Name: value\".\n"
			);
		run.Ran.Should().BeEmpty();
	}

	[Test]
	public void TheRerunCommandCarriesTheSiteAndFlags()
	{
		var args = new JourneyRunArgs { Features = ["preview"], Env = "demo", ReadOnly = true, UserAgent = "Monitor 1", Viewport = "1280x800" };

		JourneyCli
			.RerunCommand("https://shop.test/", null, "off", args)
			.Should()
			.Be(
				"snap journey run {id}.journey --base-url https://shop.test/ --transcript off --env demo --feature preview --read-only --user-agent \"Monitor 1\" --viewport 1280x800"
			);
	}

	[Test]
	public async Task NoChromeExitsThree()
	{
		var run = new Run();
		run.Transcript = _ => throw new Cdp.BrowserLaunchException("No Chrome found. Looked in:\n  /usr/bin/google-chrome   (install path)");

		(await run.Invoke(null, "journeys/shop.journey")).Should().Be(3);

		run.Err.ToString().Should().StartWith("Error: No Chrome found.");
	}
}
