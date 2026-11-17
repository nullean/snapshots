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
	private sealed class Fake(
		Func<string, JourneySnapshotOptions, JourneyTranscript> run,
		Func<JourneyPrecondition, PreconditionResult>? wait = null
	) : JourneyConsoleRunner.IJourneyExecutor
	{
		public Task EnsureReadyAsync(CancellationToken cancellationToken) => Task.CompletedTask;

		public Task<PreconditionResult> WaitAsync(JourneyPrecondition precondition, JourneyOptions options, CancellationToken cancellationToken) =>
			Task.FromResult(wait?.Invoke(precondition) ?? new PreconditionResult(true, "ok", 1, TimeSpan.Zero));

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
		bool interactive = false,
		Func<JourneyPrecondition, PreconditionResult>? wait = null
	)
	{
		var output = new StringWriter();
		var error = new StringWriter();
		run ??= (file, _) => new JourneyTranscript(file, "", null);
		var exit = await JourneyConsoleRunner.RunAsync(
			options,
			output,
			error,
			new Fake(run, wait),
			new ConsoleJourneyReporter(output, options, interactive),
			CancellationToken.None
		);
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
				? new JourneyTranscript(
					file,
					"",
					new JourneyFailure(2, "click #pay", "Timed out after 5s waiting for css #pay to be visible: no element matches.")
				)
				: new JourneyTranscript(file, "", null);
		});

		exit.Should().Be(JourneyExitCode.Failed);
		output.Should().MatchInlineSnapshot(
			"""
			ok      shop/basket (<t>s)
			FAILED  shop/pay

			Journey stopped at journeys/shop/pay.journey:2: click #pay
			Timed out after 5s waiting for css #pay to be visible: no element matches.
			Last open: https://shop.test/pay
			Rerun:     snap journey run --only shop/pay

			1 passed, 1 failed, 0 skipped in <t>s, against https://shop.test/
			"""
		);
	}

	private static JourneyTranscript Stopped(string file) =>
		new(file, "", new JourneyFailure(2, "click #pay", "Timed out after 30s waiting for the URL to match '**/pay'."));

	[Test]
	public async Task AJourneyThatPassesOnARetryPassesTheRunAndIsReportedAsFlaky()
	{
		using var folder = new Folder();
		var options = new JourneyRunOptions
		{
			Files = [folder.Add("shop/pay", "open /\nclick #pay")],
			BaseUrl = new Uri("https://shop.test/"),
			Rerun = "snap journey run --only {id}",
			Retries = 2,
			GitHubActions = false,
		};
		var runs = 0;

		var (exit, output, _) = await RunAsync(options, (file, _) => ++runs == 1 ? Stopped(file) : new JourneyTranscript(file, "", null));

		exit.Should().Be(JourneyExitCode.Ok);
		runs.Should().Be(2);
		output.Should().MatchInlineSnapshot(
			"""
			retry   shop/pay (attempt 1 of 3 failed at line 2)
			flaky   shop/pay (<t>s, passed on attempt 2)

			FLAKY   shop/pay: passed on attempt 2, after 1 failed attempt
			Attempt 1:
			Journey stopped at journeys/shop/pay.journey:2: click #pay
			Timed out after 30s waiting for the URL to match '**/pay'.
			Rerun:     snap journey run --only shop/pay

			1 passed (1 flaky), 0 failed, 0 skipped in <t>s, against https://shop.test/
			"""
		);
	}

	[Test]
	public async Task AJourneyThatKeepsFailingFailsAfterTheLastRetry()
	{
		using var folder = new Folder();
		var options = new JourneyRunOptions { Files = [folder.Add("shop/pay", "open /\nclick #pay")], Retries = 1, GitHubActions = false };
		var runs = 0;

		var (exit, output, _) = await RunAsync(options, (file, _) =>
		{
			runs++;
			return Stopped(file);
		});

		exit.Should().Be(JourneyExitCode.Failed);
		runs.Should().Be(2);
		output.Should().Contain("retry   shop/pay (attempt 1 of 2 failed at line 2)").And.Contain("FAILED  shop/pay").And.NotContain("FLAKY");
	}

	[Test]
	public async Task NoRetriesByDefaultAndNoneForASourceThatCannotBeParsed()
	{
		using var folder = new Folder();
		var plain = new JourneyRunOptions { Files = [folder.Add("a/one", "open /")], GitHubActions = false };
		var broken = new JourneyRunOptions { Files = [folder.Add("a/broken", "frobnicate")], Retries = 3, GitHubActions = false };
		var runs = 0;

		var first = await RunAsync(plain, (file, _) =>
		{
			runs++;
			return Stopped(file);
		});
		var second = await RunAsync(broken, (file, _) =>
		{
			runs++;
			return new JourneyTranscript(file, "", null);
		});

		first.Exit.Should().Be(JourneyExitCode.Failed);
		first.Output.Should().NotContain("retry");
		second.Exit.Should().Be(JourneyExitCode.Usage);
		runs.Should().Be(1);
	}

	[Test]
	public async Task AFlakyJourneyIsAWarningAnnotationAndAFlakyFailureInJUnit()
	{
		using var folder = new Folder();
		var junit = Path.Combine(folder.Path, "junit.xml");
		var summary = Path.Combine(folder.Path, "summary.md");
		var options = new JourneyRunOptions
		{
			Files = [folder.Add("shop/pay", "open /\nclick #pay")],
			Retries = 1,
			JUnitPath = junit,
			StepSummaryPath = summary,
			GitHubActions = true,
		};
		var runs = 0;

		var (exit, output, _) = await RunAsync(options, (file, _) => ++runs == 1 ? Stopped(file) : new JourneyTranscript(file, "", null));

		exit.Should().Be(JourneyExitCode.Ok);
		output.Should().Contain("::warning file=journeys/shop/pay.journey,line=2,title=Journey flaky%3A shop/pay passed on attempt 2::");
		var xml = File.ReadAllText(junit);
		xml.Should().Contain("<flakyFailure message=\"click #pay\">").And.NotContain("<failure");
		File
			.ReadAllText(summary)
			.Should()
			.Contain("✅ **1 passed** (⚠️ 1 flaky)")
			.And
			.Contain("⚠️ flaky, passed on attempt 2")
			.And
			.Contain("#### ⚠️ shop/pay (flaky: attempt 1 failed, attempt 2 passed)");
	}

	private static readonly JourneyPrecondition Build = new()
	{
		Path = "/docs",
		Selector = "meta[name='docs:build']",
		Attribute = "content",
		Expected = "1.74.1",
	};

	[Test]
	public async Task AMetPreconditionIsWaitedForBeforeTheJourneysRun()
	{
		using var folder = new Folder();
		var options = new JourneyRunOptions { Files = [folder.Add("shop/basket", "open /")], Preconditions = [Build], GitHubActions = false };
		var order = new List<string>();

		var (exit, output, _) = await RunAsync(options, (file, _) =>
		{
			order.Add("journey");
			return new JourneyTranscript(file, "", null);
		}, wait: p =>
		{
			order.Add("wait");
			return new PreconditionResult(true, "The content attribute of meta[name='docs:build'] on /docs is \"1.74.1\".", 2, TimeSpan.FromSeconds(1.5));
		});

		exit.Should().Be(JourneyExitCode.Ok);
		order.Should().Equal("wait", "journey");
		output.Should().MatchInlineSnapshot(
			"""
			waiting for the content attribute of meta[name='docs:build'] on /docs (10s)
			ready   The content attribute of meta[name='docs:build'] on /docs is "1.74.1". (2 looks, <t>s)
			ok      shop/basket (<t>s)

			1 passed, 0 failed, 0 skipped in <t>s
			"""
		);
	}

	[Test]
	public async Task AnUnmetPreconditionEndsTheRunWithItsOwnExitCodeAndRunsNoJourney()
	{
		using var folder = new Folder();
		var junit = Path.Combine(folder.Path, "junit.xml");
		var options = new JourneyRunOptions
		{
			Files = [folder.Add("shop/basket", "open /")],
			Preconditions = [Build],
			JUnitPath = junit,
			StepSummary = false,
			GitHubActions = true,
		};
		var ran = false;

		var (exit, output, error) = await RunAsync(
			options,
			(file, _) =>
			{
				ran = true;
				return new JourneyTranscript(file, "", null);
			},
			wait: _ => new PreconditionResult(
				false,
				"Timed out after 10s waiting for the content attribute of meta[name='docs:build'] on /docs to equal \"1.74.1\": last saw \"1.73.0\" in 20 looks.",
				20,
				TimeSpan.FromSeconds(10)
			)
		);

		exit.Should().Be(JourneyExitCode.Precondition);
		ran.Should().BeFalse();
		error.Should().StartWith("Error: Timed out after 10s waiting for the content attribute").And.Contain("last saw \"1.73.0\"");
		output.Should().Contain("::error title=The site is not ready%3A no journey ran::Timed out after 10s");
		var xml = File.ReadAllText(junit);
		xml
			.Should()
			.Contain("name=\"wait\"")
			.And
			.Contain("classname=\"preconditions\"")
			.And
			.Contain("<failure")
			.And
			.Contain("<failure message=\"journey did not run\">Timed out after 10s")
			.And
			.Contain("last saw \"1.73.0\"");
	}

	[Test]
	public async Task PreconditionsAreWaitedForInOrderAndTheFirstUnmetOneStopsTheRun()
	{
		using var folder = new Folder();
		var second = Build with { Header = null, Selector = "h1", Attribute = null, Expected = "x" };
		var options = new JourneyRunOptions { Files = [folder.Add("a/one", "open /")], Preconditions = [Build, second, Build], GitHubActions = false };
		var asked = new List<string?>();

		var (exit, _, error) = await RunAsync(options, wait: p =>
		{
			asked.Add(p.Selector);
			return p == second ? new PreconditionResult(false, "second is not met", 1, TimeSpan.Zero) : new PreconditionResult(true, "ok", 1, TimeSpan.Zero);
		});

		exit.Should().Be(JourneyExitCode.Precondition);
		asked.Should().Equal("meta[name='docs:build']", "h1");
		error.Should().Be("Error: second is not met" + Environment.NewLine);
	}

	[Test]
	public async Task OptionsAreRefreshedOnceThePreconditionsAreMetSoTheJourneysAreJudgedAgainstTheNewBuild()
	{
		using var folder = new Folder();
		// The deploy that the wait is for turns a flag on: before it the journey is skipped, after it the journey runs.
		var stale = new JourneyRunOptions
		{
			Files = [folder.Add("a/needs", "# @requires navigation-preview\nopen /")],
			Preconditions = [Build],
			GitHubActions = false,
			RefreshAfterPreconditions =
				(current, _) => Task.FromResult(new JourneyRunOptions
				{
					Files = current.Files,
					Features = new HashSet<string> { "navigation-preview" },
					GitHubActions = false
				}),
		};
		var waited = false;

		var (exit, output, _) = await RunAsync(stale, wait: _ =>
		{
			waited = true;
			return new PreconditionResult(true, "ok", 1, TimeSpan.Zero);
		});

		exit.Should().Be(JourneyExitCode.Ok);
		waited.Should().BeTrue();
		output.Should().Contain("ok      a/needs").And.NotContain("skipped a/needs");
	}

	[Test]
	public async Task OptionsAreNotRefreshedWhenAPreconditionIsNotMetOrThereIsNone()
	{
		using var folder = new Folder();
		var refreshed = 0;

		Task<JourneyRunOptions> Refresh(JourneyRunOptions current, CancellationToken _)
		{
			refreshed++;
			return Task.FromResult(current);
		}

		var unmet = new JourneyRunOptions
		{
			Files = [folder.Add("a/one", "open /")],
			Preconditions = [Build],
			RefreshAfterPreconditions = Refresh,
			GitHubActions = false
		};
		var none = new JourneyRunOptions { Files = [folder.Add("a/two", "open /")], RefreshAfterPreconditions = Refresh, GitHubActions = false };

		var first = await RunAsync(unmet, wait: _ => new PreconditionResult(false, "no", 1, TimeSpan.Zero));
		var second = await RunAsync(none);

		first.Exit.Should().Be(JourneyExitCode.Precondition);
		second.Exit.Should().Be(JourneyExitCode.Ok);
		refreshed.Should().Be(0);
	}

	[Test]
	public async Task APreconditionIsWaitedForBeforeThePlanEvenWhenEveryJourneyIsSkipped()
	{
		using var folder = new Folder();
		var options = new JourneyRunOptions
		{
			Files = [folder.Add("a/needs", "# @requires build-type:isolated\nopen /")],
			BuildType = "assembler",
			Preconditions = [Build],
			AllowAllSkipped = true,
			GitHubActions = false,
		};

		var (exit, output, error) = await RunAsync(options, wait: _ => new PreconditionResult(false, "not yet", 1, TimeSpan.Zero));

		exit.Should().Be(JourneyExitCode.Precondition);
		error.Should().StartWith("Error: not yet");
		output.Should().NotContain("skipped a/needs");
	}

	[Test]
	public async Task SkipsAreCollapsedPerReasonUnlessVerbose()
	{
		using var folder = new Folder();
		var files = Enumerable
			.Range(0, 5)
			.Select(i => folder.Add($"a/needs{i}", "# @requires build-type:isolated\nopen /"))
			.Append(folder.Add("a/two", "# @requires env:prod\nopen /"))
			.Append(folder.Add("a/runs", "open /"))
			.ToList();
		var options = new JourneyRunOptions { Files = files, BuildType = "assembler", Env = "demo" };

		var collapsed = await RunAsync(options);
		var verbose = await RunAsync(new JourneyRunOptions { Files = files, BuildType = "assembler", Env = "demo", Verbose = true });

		collapsed
			.Output
			.Should()
			.MatchInlineSnapshot(
				"""
			skipped 5 journeys (requires build-type:isolated); --verbose lists them
			skipped a/two (requires env:prod)
			ok      a/runs (<t>s)

			1 passed, 0 failed, 6 skipped in <t>s
			"""
			);
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
		strict.Output.Should().EndWith("Every selected journey was skipped, so nothing was checked.\n");
		lenient.Output.Should().EndWith("Every selected journey was skipped; none applies to this site.\n");
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
		var message =
			"Journey transcript did not match snapshot: journeys/a/shop.verified.journey\n1 difference(s):\n- Shop\n+ Store\nReceived: .artifacts/a/shop.received.journey\nAccept:   snap accept a/shop";
		var options = new JourneyRunOptions { Files = files, Compare = (file, _) => new SnapshotResult(false, message, "a/shop", "v", "r"), };

		var (exit, output, _) = await RunAsync(options);

		exit.Should().Be(JourneyExitCode.Failed);
		output.Should().MatchInlineSnapshot(
			"""
			FAILED  a/shop

			Journey transcript did not match snapshot: journeys/a/shop.verified.journey
			1 difference(s):
			- Shop
			+ Store
			Received: .artifacts/a/shop.received.journey
			Accept:   snap accept a/shop

			0 passed, 1 failed, 0 skipped in <t>s
			"""
		);
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

		var (exit, output, error) = await RunAsync(new JourneyRunOptions { Files = files }, (_, _) => throw new BrowserLaunchException("No Chrome found."));

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
			(file, _) => new JourneyTranscript(
				file,
				"",
				new JourneyFailure(3, "click #pay", "Timed out after 5s waiting for css #pay to be visible: no element matches.")
			),
			interactive: true
		);

		var plain = Ansis().Replace(output, "");
		plain
			.Should()
			.Contain("journeys/a/pay.journey:3")
			.And
			.Contain("3 ")
			.And
			.Contain("click #pay")
			.And
			.Contain("get title")
			.And
			.Contain("Timed out after 5s waiting for css #pay to be visible: no element matches.")
			.And
			.Contain("rerun")
			.And
			.Contain("snap journey run --only a/pay");
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

	private static byte[] Png() => PngEncoder.Encode(new RgbaImage(4, 4, Enumerable.Range(0, 16).SelectMany(_ => new byte[] { 200, 40, 40, 255 }).ToArray()));

	private static JourneyTranscript Failed(string file, bool withScreenshot) =>
		new(
			file,
			"",
			new JourneyFailure(3, "click #pay", "Timed out after 5s waiting for css #pay to be visible: no element matches.")
			{
				Url = "https://shop.test/pay",
				Screenshot = withScreenshot ? Png() : null,
				ScreenshotPath = withScreenshot ? "/tmp/art/failure.png" : null,
			}
		);

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

		var (_, output, _) = await RunAsync(
			new JourneyRunOptions { Files = files, BaseUrl = new Uri("https://shop.test/"), ScreenshotOnFailure = true },
			(file, _) => Failed(file, withScreenshot: true)
		);

		output
			.Should()
			.Contain("Page:       https://shop.test/pay")
			.And
			.Contain("Screenshot: /tmp/art/failure.png")
			.And
			.NotContain("Last open:")
			.And
			.NotContain("\u2580");
	}

	[Test]
	public async Task AnInteractiveFailureDrawsTheScreenshotUnderTheErrorAndBeforeThePage()
	{
		using var folder = new Folder();
		var files = new[] { folder.Add("a/pay", "open /\nclick #pay") };
		var options = new JourneyRunOptions
		{
			Files = files,
			BaseUrl = new Uri("https://shop.test/"),
			ScreenshotOnFailure = true,
			Rerun = "snap journey run {id}.journey"
		};

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

		var (_, output, _) = await RunAsync(
			new JourneyRunOptions { Files = files, BaseUrl = new Uri("https://shop.test/") },
			(file, _) => Failed(file, withScreenshot: true),
			interactive: true
		);

		output.Should().NotContain("\u2580");
	}

	private static JourneyTranscript Broken(string file, int line = 3) =>
		new(file, "", new JourneyFailure(line, "click #pay", "Timed out after 5s: no element matches.\nsecond line") { Url = "https://shop.test/pay", });

	[Test]
	public void WorkflowCommandDataAndPropertiesAreEscaped()
	{
		GitHubActions.Escape("100%\r\nnext").Should().Be("100%25%0D%0Anext");
		GitHubActions.EscapeProperty("a: b, c").Should().Be("a%3A b%2C c");
	}

	[Test]
	public async Task AFailedJourneyGetsAnErrorAnnotationAtItsStepAndASkippedOneGetsNone()
	{
		using var folder = new Folder();
		var files = new[] { folder.Add("a/pay", "open /\nclick #pay"), folder.Add("a/ok", "open /"), folder.Add("a/other", "# @requires env:prod\nopen /"), };

		var (_, output, _) = await RunAsync(
			new JourneyRunOptions { Files = files, GitHubActions = true, Env = "demo" },
			(file, _) => file.EndsWith("pay.journey", StringComparison.Ordinal) ? Broken(file) : new JourneyTranscript(file, "", null)
		);

		var annotations = output.Split('\n').Where(l => l.StartsWith("::", StringComparison.Ordinal)).ToList();
		annotations.Should().Equal(
			"::error file=journeys/a/pay.journey,line=3,title=Journey failed%3A a/pay::click #pay%0ATimed out after 5s: no element matches.%0Asecond line%0APage: https://shop.test/pay"
		);
		output.Should().NotContain("::group::", "a few failures stay expanded");
	}

	[Test]
	public async Task AnnotationsStopAtTheLimitAndFailuresFoldIntoGroupsWhenThereAreSeveral()
	{
		using var folder = new Folder();
		var files = Enumerable.Range(0, 12).Select(i => folder.Add($"a/f{i:00}", "open /")).ToList();

		var (_, output, _) = await RunAsync(new JourneyRunOptions { Files = files, GitHubActions = true }, (file, _) => Broken(file));

		var lines = output.Split('\n');
		lines.Count(l => l.StartsWith("::error ", StringComparison.Ordinal)).Should().Be(10);
		lines.Should().Contain("::warning title=Journeys::2 more failed journeys have no annotation: see the step summary.");
		lines.Count(l => l.StartsWith("::group::FAILED  a/f", StringComparison.Ordinal)).Should().Be(12);
		lines.Count(l => l == "::endgroup::").Should().Be(12);
	}

	[Test]
	public async Task NothingIsPrintedForGitHubWhenItIsOff()
	{
		using var folder = new Folder();
		var files = new[] { folder.Add("a/pay", "open /") };

		var (_, output, _) = await RunAsync(new JourneyRunOptions { Files = files, GitHubActions = false }, (file, _) => Broken(file));

		output.Should().NotContain("::");
	}

	[Test]
	public async Task TheSummaryHasOneLineOfTotalsOneForSkipsAndAFailureSection()
	{
		using var folder = new Folder();
		var files = new[]
		{
			folder.Add("a/pay", "open /\nclick #pay"),
			folder.Add("a/ok", "open /"),
			folder.Add("a/needs0", "# @requires env:prod\nopen /"),
			folder.Add("a/needs1", "# @requires env:prod\nopen /"),
		};
		var summary = Path.Combine(folder.Path, "summary.md");

		await RunAsync(
			new JourneyRunOptions
			{
				Files = files,
				Env = "demo",
				BaseUrl = new Uri("https://shop.test/"),
				StepSummaryPath = summary,
				Rerun = "snap journey run {id}.journey"
			},
			(file, _) => file.EndsWith("pay.journey", StringComparison.Ordinal) ? Broken(file) : new JourneyTranscript(file, "", null)
		);

		Normalise(File.ReadAllText(summary))
			.Should()
			.MatchInlineSnapshot(
				"""
			### Journeys against https://shop.test/

			✅ **1 passed** · ❌ **1 failed** · ⊘ 2 skipped

			⊘ Skipped `a/needs0` (requires env:prod), `a/needs1` (requires env:prod)

			| Journey | Result | Time |
			|---|---|---|
			| `a/pay` | **failed** | <t>s |
			| `a/ok` | passed | <t>s |

			#### ❌ a/pay

			`journeys/a/pay.journey:3`: `click #pay`

			```text
			Timed out after 5s: no element matches.
			second line
			```

			Page: https://shop.test/pay · Rerun: `snap journey run a/pay.journey`
			"""
			);
	}

	[Test]
	public async Task AManyJourneyRunListsOnlyTheFailuresAndCountsTheSkipsPerReason()
	{
		using var folder = new Folder();
		var files = Enumerable
			.Range(0, 12)
			.Select(i => folder.Add($"a/ok{i:00}", "open /"))
			.Append(folder.Add("a/pay", "open /\nclick #pay"))
			.Concat(Enumerable.Range(0, 4).Select(i => folder.Add($"b/skip{i}", "# @requires env:prod\nopen /")))
			.Append(folder.Add("b/codex", "# @requires build-type:codex\nopen /"))
			.ToList();
		var summary = Path.Combine(folder.Path, "summary.md");

		await RunAsync(
			new JourneyRunOptions { Files = files, Env = "demo", StepSummaryPath = summary },
			(file, _) => file.EndsWith("pay.journey", StringComparison.Ordinal) ? Broken(file) : new JourneyTranscript(file, "", null)
		);
		var text = File.ReadAllText(summary);

		text.Should().Contain("⊘ Skipped 5: requires env:prod (4), requires build-type:codex (1)");
		text.Should().NotContain("`a/ok00`", "a long run lists the failures, not every journey");
	}
}
