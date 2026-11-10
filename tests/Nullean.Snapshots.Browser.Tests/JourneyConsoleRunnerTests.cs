using System.Text.RegularExpressions;
using AwesomeAssertions;
using Nullean.Cdp;
using Nullean.Cdp.Journeys;
using Nullean.Snapshots.Browser.Console;
using Nullean.TerminalImages;
using Nullean.TerminalImages.Png;

namespace Nullean.Snapshots.Browser.Tests;

/// <summary>The console runner's selection, output and exit codes, with a fake executor in place of Chrome.</summary>
[NotInParallel("console-runner")]
public partial class JourneyConsoleRunnerTests
{
	private sealed class Fake(Func<string, JourneySnapshotOptions, JourneyTranscript> run) : JourneyConsoleRunner.IJourneyExecutor
	{
		public Task EnsureReadyAsync(CancellationToken cancellationToken) => Task.CompletedTask;

		public Task<JourneyTranscript> RunAsync(string file, JourneySnapshotOptions options, CancellationToken cancellationToken) =>
			Task.FromResult(run(file, options));
	}

	private sealed class Folder : IDisposable
	{
		public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "console-runner-" + Guid.NewGuid().ToString("N"));

		public Folder() => Directory.CreateDirectory(Path);

		public JourneyRunFile Add(string id, string text)
		{
			var file = System.IO.Path.Combine(Path, id + ".journey");
			Directory.CreateDirectory(System.IO.Path.GetDirectoryName(file)!);
			File.WriteAllText(file, text);
			return new JourneyRunFile(id, file) { Display = $"journeys/{id}.journey" };
		}

		public void Dispose() => Directory.Delete(Path, recursive: true);
	}

	private static async Task<(int Exit, string Output, string Error)> RunAsync(
		JourneyRunOptions options,
		Func<string, JourneySnapshotOptions, JourneyTranscript>? run = null,
		bool interactive = false
	)
	{
		var output = new StringWriter();
		var error = new StringWriter();
		run ??= (file, _) => new JourneyTranscript(file, "", null);
		var exit = await JourneyConsoleRunner.RunAsync(options, output, error, new Fake(run), new ConsoleJourneyReporter(output, options, interactive), CancellationToken.None);
		return (exit, Normalise(output.ToString()), error.ToString());
	}

	// Times differ per machine.
	private static string Normalise(string text) => Seconds().Replace(text.ReplaceLineEndings("\n"), "<t>s");

	[GeneratedRegex(@"\d+\.\ds")]
	private static partial Regex Seconds();

	[Test]
	public async Task APlainRunStreamsOneLinePerJourneyThenShowsTheFailureAndTheTotals()
	{
		using var folder = new Folder();
		var ok = folder.Add("shop/basket", "open /");
		var broken = folder.Add("shop/pay", "open /\nclick #pay\nget title");
		var options = new JourneyRunOptions
		{
			Files = [ok, broken],
			BaseUrl = new Uri("https://shop.test/"),
			Rerun = "snap journey run --only {id}",
			Parallel = 1,
		};

		var (exit, output, _) = await RunAsync(options, (file, o) =>
		{
			o.OnStep?.Invoke(new JourneyStep(1, ["open", "/pay"], new OpenCommand("/pay")));
			return file.EndsWith("pay.journey", StringComparison.Ordinal)
				? new JourneyTranscript(file, "", new JourneyFailure(2, "click #pay", "Timed out after 5s waiting for css #pay to be visible: no element matches."))
				: new JourneyTranscript(file, "", null);
		});

		exit.Should().Be(JourneyExitCode.Failed);
		output.Should().MatchInlineSnapshot("""
			ok      shop/basket (<t>s)
			FAILED  shop/pay

			Journey stopped at journeys/shop/pay.journey:2: click #pay
			Timed out after 5s waiting for css #pay to be visible: no element matches.
			Last open: https://shop.test/pay
			Rerun:     snap journey run --only shop/pay

			1 passed, 1 failed, 0 skipped in <t>s, against https://shop.test/
			""");
	}

	[Test]
	public async Task SkipsAreCollapsedPerReasonUnlessVerbose()
	{
		using var folder = new Folder();
		var files = Enumerable.Range(0, 5).Select(i => folder.Add($"a/needs{i}", "# @requires build-type:isolated\nopen /"))
			.Append(folder.Add("a/two", "# @requires env:prod\nopen /"))
			.Append(folder.Add("a/runs", "open /")).ToList();
		var options = new JourneyRunOptions { Files = files, BuildType = "assembler", Env = "demo" };

		var collapsed = await RunAsync(options);
		var verbose = await RunAsync(new JourneyRunOptions { Files = files, BuildType = "assembler", Env = "demo", Verbose = true });

		collapsed.Output.Should().MatchInlineSnapshot("""
			skipped 5 journeys (requires build-type:isolated); --verbose lists them
			skipped a/two (requires env:prod)
			ok      a/runs (<t>s)

			1 passed, 0 failed, 6 skipped in <t>s
			""");
		verbose.Output.Should().Contain("skipped a/needs0 (requires build-type:isolated)").And.Contain("skipped a/needs4 (requires build-type:isolated)");
	}

	[Test]
	public async Task TagsAndMutationsSelectJourneys()
	{
		using var folder = new Folder();
		var files = new[]
		{
			folder.Add("a/smoke", "# @tags smoke\nopen /"),
			folder.Add("a/slow", "# @tags slow\nopen /"),
			folder.Add("a/writes", "# @mutates\nopen /"),
		};

		var tagged = await RunAsync(new JourneyRunOptions { Files = files, Tags = new HashSet<string> { "smoke" } });
		var readOnly = await RunAsync(new JourneyRunOptions { Files = files, ReadOnly = true });
		var allowed = await RunAsync(new JourneyRunOptions { Files = files, ReadOnly = true, AllowMutations = true });

		tagged.Output.Should().Contain("ok      a/smoke").And.Contain("skipped a/slow (no tag in [smoke])");
		readOnly.Output.Should().Contain("skipped a/writes (mutates data; a read-only run skips it)");
		allowed.Output.Should().Contain("ok      a/writes");
	}

	[Test]
	public async Task ARunThatCheckedNothingFailsUnlessAllSkippedIsAllowed()
	{
		using var folder = new Folder();
		var files = new[] { folder.Add("a/only", "# @requires build-type:isolated\nopen /") };

		var strict = await RunAsync(new JourneyRunOptions { Files = files, BuildType = "assembler" });
		var lenient = await RunAsync(new JourneyRunOptions { Files = files, BuildType = "assembler", AllowAllSkipped = true });

		strict.Exit.Should().Be(JourneyExitCode.Failed);
		lenient.Exit.Should().Be(JourneyExitCode.Ok);
	}

	[Test]
	public async Task AJourneyThatCannotBeParsedFailsAloneAndTheOthersStillRun()
	{
		using var folder = new Folder();
		var files = new[] { folder.Add("a/fine", "open /"), folder.Add("a/broken", "this is not a command") };

		var usage = await RunAsync(new JourneyRunOptions { Files = files });
		var asFailure = await RunAsync(new JourneyRunOptions { Files = files, ParseErrorExitCode = JourneyExitCode.Failed });

		usage.Exit.Should().Be(JourneyExitCode.Usage);
		asFailure.Exit.Should().Be(JourneyExitCode.Failed);
		usage.Output.Should().Contain("ok      a/fine").And.Contain("FAILED  a/broken").And.Contain("Journey did not run: journeys/a/broken.journey");
	}

	[Test]
	public async Task ATranscriptThatDiffersShowsTheComparisonsOwnMessage()
	{
		using var folder = new Folder();
		var files = new[] { folder.Add("a/shop", "open /") };
		var message = "Journey transcript did not match snapshot: journeys/a/shop.verified.journey\n1 difference(s):\n- Shop\n+ Store\nReceived: .artifacts/a/shop.received.journey\nAccept:   snap accept a/shop";
		var options = new JourneyRunOptions
		{
			Files = files,
			Compare = (file, _) => new SnapshotResult(false, message, "a/shop", "v", "r"),
		};

		var (exit, output, _) = await RunAsync(options);

		exit.Should().Be(JourneyExitCode.Failed);
		output.Should().MatchInlineSnapshot("""
			FAILED  a/shop

			Journey transcript did not match snapshot: journeys/a/shop.verified.journey
			1 difference(s):
			- Shop
			+ Store
			Received: .artifacts/a/shop.received.journey
			Accept:   snap accept a/shop

			0 passed, 1 failed, 0 skipped in <t>s
			""");
	}

	[Test]
	public async Task AStepFailureStillComparesSoTheReceivedFileIsWrittenButShowsTheStep()
	{
		using var folder = new Folder();
		var files = new[] { folder.Add("a/shop", "open /\nget title") };
		var compared = false;
		var options = new JourneyRunOptions
		{
			Files = files,
			Compare = (_, _) =>
			{
				compared = true;
				return new SnapshotResult(false, "partial transcript differs", "k", "v", "r");
			},
		};

		var (_, output, _) = await RunAsync(options, (file, _) => new JourneyTranscript(file, "", new JourneyFailure(1, "open /", "boom")));

		compared.Should().BeTrue();
		output.Should().Contain("Journey stopped at journeys/a/shop.journey:1: open /").And.NotContain("partial transcript differs");
	}

	[Test]
	public async Task ABrowserThatCannotStartEndsTheRunWithTheBrowserExitCode()
	{
		using var folder = new Folder();
		var files = new[] { folder.Add("a/shop", "open /") };

		var (exit, output, error) = await RunAsync(
			new JourneyRunOptions { Files = files },
			(_, _) => throw new BrowserLaunchException("No Chrome found."));

		exit.Should().Be(JourneyExitCode.Browser);
		error.Should().Be("Error: No Chrome found.\n".ReplaceLineEndings());
		output.Should().NotContain("FAILED");
	}

	[Test]
	public async Task ALoaderReplacesReadingTheFileFromDisk()
	{
		var file = new JourneyRunFile("a/virtual", "/no/such/file.journey");
		var options = new JourneyRunOptions { Files = [file], Load = path => Journey.Parse("# @requires env:prod\nopen /", path), Env = "demo" };

		var (_, output, _) = await RunAsync(options);

		output.Should().Contain("skipped a/virtual (requires env:prod)");
	}

	[Test]
	public async Task TheInteractiveFailureShowsTheJourneyAroundTheFailingLine()
	{
		using var folder = new Folder();
		var files = new[] { folder.Add("a/pay", "open /\n# a comment\nclick #pay\nget title") };
		var options = new JourneyRunOptions { Files = files, BaseUrl = new Uri("https://shop.test/"), Rerun = "snap journey run --only {id}" };

		var (_, output, _) = await RunAsync(
			options,
			(file, _) => new JourneyTranscript(file, "", new JourneyFailure(3, "click #pay", "Timed out after 5s waiting for css #pay to be visible: no element matches.")),
			interactive: true);

		var plain = Ansis().Replace(output, "");
		plain.Should().Contain("journeys/a/pay.journey:3")
			.And.Contain("3 ").And.Contain("click #pay").And.Contain("get title")
			.And.Contain("Timed out after 5s waiting for css #pay to be visible: no element matches.")
			.And.Contain("rerun").And.Contain("snap journey run --only a/pay");
	}

	[GeneratedRegex("\\e\\[[0-9;]*[A-Za-z]|\r")]
	private static partial Regex Ansis();

	[Test]
	public async Task AJUnitReportListsEveryJourney()
	{
		using var folder = new Folder();
		var files = new[] { folder.Add("a/ok", "open /"), folder.Add("a/skip", "# @requires env:prod\nopen /") };
		var junit = Path.Combine(folder.Path, "out", "junit.xml");

		await RunAsync(new JourneyRunOptions { Files = files, JUnitPath = junit, BaseUrl = new Uri("https://shop.test/"), Env = "demo" });

		var xml = File.ReadAllText(junit);
		xml.Should().Contain("tests=\"2\"").And.Contain("skipped=\"1\"").And.Contain("classname=\"a\"").And.Contain("<skipped message=\"requires env:prod\"");
	}

	[Test]
	public void WrapBreaksAtSpacesAndKeepsALongWordWhole()
	{
		ConsoleJourneyReporter.Wrap("aaa bbb ccc", 7).Should().Equal("aaa bbb", "ccc");
		ConsoleJourneyReporter.Wrap("averyveryverylongword x", 5).Should().Equal("averyveryverylongword", "x");
	}

	[Test]
	public async Task TheStepSummaryIsWrittenOnlyWhenAskedForAndAFailureToWriteItDoesNotFailTheRun()
	{
		using var folder = new Folder();
		var files = new[] { folder.Add("a/ok", "open /") };
		var summary = Path.Combine(folder.Path, "summary.md");

		await RunAsync(new JourneyRunOptions { Files = files, StepSummary = false, StepSummaryPath = summary });
		File.Exists(summary).Should().BeFalse();

		await RunAsync(new JourneyRunOptions { Files = files, BaseUrl = new Uri("https://shop.test/"), StepSummaryPath = summary });
		File.ReadAllText(summary).Should().Contain("### Journeys against https://shop.test/").And.Contain("| `a/ok` | passed |");

		// A folder where the file should be: appending fails, and the run still ends normally.
		var blocked = Path.Combine(folder.Path, "blocked");
		Directory.CreateDirectory(blocked);
		(await RunAsync(new JourneyRunOptions { Files = files, StepSummaryPath = blocked })).Exit.Should().Be(JourneyExitCode.Ok);
	}

	private static byte[] Png() =>
		PngEncoder.Encode(new RgbaImage(4, 4, Enumerable.Range(0, 16).SelectMany(_ => new byte[] { 200, 40, 40, 255 }).ToArray()));

	private static JourneyTranscript Failed(string file, bool withScreenshot) => new(file, "", new JourneyFailure(3, "click #pay", "Timed out after 5s waiting for css #pay to be visible: no element matches.")
	{
		Url = "https://shop.test/pay",
		Screenshot = withScreenshot ? Png() : null,
		ScreenshotPath = withScreenshot ? "/tmp/art/failure.png" : null,
	});

	[Test]
	public async Task TheScreenshotOptionReachesTheJourneyRun()
	{
		using var folder = new Folder();
		var files = new[] { folder.Add("a/pay", "open /") };
		var seen = new List<bool>();

		await RunAsync(new JourneyRunOptions { Files = files, ScreenshotOnFailure = true }, (file, o) =>
		{
			seen.Add(o.ScreenshotOnFailure);
			return new JourneyTranscript(file, "", null);
		});
		await RunAsync(new JourneyRunOptions { Files = files }, (file, o) =>
		{
			seen.Add(o.ScreenshotOnFailure);
			return new JourneyTranscript(file, "", null);
		});

		seen.Should().Equal(true, false);
	}

	[Test]
	public async Task APlainFailureNamesThePageAndTheScreenshotFileAndNothingIsDrawn()
	{
		using var folder = new Folder();
		var files = new[] { folder.Add("a/pay", "open /\nclick #pay") };

		var (_, output, _) = await RunAsync(new JourneyRunOptions { Files = files, BaseUrl = new Uri("https://shop.test/"), ScreenshotOnFailure = true }, (file, _) => Failed(file, withScreenshot: true));

		output.Should().Contain("Page:       https://shop.test/pay").And.Contain("Screenshot: /tmp/art/failure.png")
			.And.NotContain("Last open:").And.NotContain("\u2580");
	}

	[Test]
	public async Task AnInteractiveFailureDrawsTheScreenshotUnderTheErrorAndBeforeThePage()
	{
		using var folder = new Folder();
		var files = new[] { folder.Add("a/pay", "open /\nclick #pay") };
		var options = new JourneyRunOptions { Files = files, BaseUrl = new Uri("https://shop.test/"), ScreenshotOnFailure = true, Rerun = "snap journey run {id}.journey" };

		var (_, output, _) = await RunAsync(options, (file, _) => Failed(file, withScreenshot: true), interactive: true);

		var plain = Ansis().Replace(output, "");
		var error = plain.IndexOf("no element matches.", StringComparison.Ordinal);
		var image = plain.IndexOf('\u2580', StringComparison.Ordinal);
		var page = plain.IndexOf("https://shop.test/pay", StringComparison.Ordinal);
		error.Should().BeGreaterThan(0);
		image.Should().BeGreaterThan(error, "the image comes after the error");
		page.Should().BeGreaterThan(image, "and before the page and rerun rows");
		plain.Should().Contain("screenshot").And.Contain("/tmp/art/failure.png").And.Contain("snap journey run a/pay.journey");
	}

	[Test]
	public async Task NoScreenshotIsDrawnWhenItWasNotAskedFor()
	{
		using var folder = new Folder();
		var files = new[] { folder.Add("a/pay", "open /\nclick #pay") };

		var (_, output, _) = await RunAsync(new JourneyRunOptions { Files = files, BaseUrl = new Uri("https://shop.test/") }, (file, _) => Failed(file, withScreenshot: true), interactive: true);

		output.Should().NotContain("\u2580");
	}
}
