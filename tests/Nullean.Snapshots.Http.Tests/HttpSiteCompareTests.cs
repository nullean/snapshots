using System.IO.Abstractions.TestingHelpers;
using AwesomeAssertions;
using Nullean.Snapshots.Cli;
using Nullean.Snapshots.Differences;
using Nullean.Snapshots.Tests;

namespace Nullean.Snapshots.Http.Tests;

/// <summary>A <c>.http</c> file run against two sites: the engine on a mock file system, the CLI with a fake runner, and two real servers.</summary>
public class HttpSiteCompareTests
{
	private static string Catalog(bool badge) => $$$"""
		## GET {{host}}/api/catalog
		status: 200 OK
		content-type: application/json; charset=utf-8

		{
		  "items": [
		    {
		{{{(badge ? "      \"badge\": \"string\",\n" : string.Empty)}}}      "price": 1.0,
		      "sku": "string"
		    }
		  ]
		}

		""".Replace("\r\n", "\n", StringComparison.Ordinal);

	private static string HttpFile(TestProject p) => p.Fs.Path.Combine(p.Project.Directory, "http", "shop.http");

	private static string Rules(TestProject p) => p.Fs.Path.Combine(p.Project.Directory, "http", "shop.differences");

	private static TestProject Project(string? rules = null, params (string Path, string Text)[] more)
	{
		var p = new TestProject();
		p.Fs.AddFile(HttpFile(p), new MockFileData("GET {{host}}/api/catalog\n"));
		if (rules is not null)
			p.Fs.AddFile(Rules(p), new MockFileData(rules));
		foreach (var (path, text) in more)
			p.Fs.AddFile(p.Fs.Path.Combine(p.Project.Directory, path), new MockFileData(text));
		return p;
	}

	private static SiteComparisonResult Compare(TestProject p, string target, SiteComparisonOptions? options = null) =>
		p.Engine.CompareSites(target, HttpSnapshot.Format, HttpRuleTargets.Instance, HttpFile(p), options ?? new SiteComparisonOptions());

	// The engine

	[Test]
	public void TheBaselineIsSeededIntoTheReceivedTreeAndNowhereElse()
	{
		var p = Project();

		var path = p.Engine.SeedBaseline(Catalog(false), "responses", HttpFile(p), null);

		path.Should().Be(p.Fs.Path.Combine(p.Project.ReceivedDirectory, "http", "shop.baseline.responses"));
		p.Read(path).Should().Be(Catalog(false));
		p.Fs.AllFiles.Should().NotContain(f => f.Contains(".verified.", StringComparison.Ordinal) || f.Contains(".received.", StringComparison.Ordinal),
			"a baseline is neither a verified nor a received snapshot");
		p.Engine.ReadBaseline("responses", HttpFile(p), null).Should().Be(Catalog(false));
		p.Engine.ReadBaseline("responses", HttpFile(p), "preview").Should().BeNull("a variant keeps its own baseline");
	}

	[Test]
	public void IdenticalTranscriptsPassAndLeaveNoTargetFile()
	{
		var p = Project();
		p.Engine.SeedBaseline(Catalog(false), "responses", HttpFile(p), null);

		var result = Compare(p, Catalog(false));

		result.Passed.Should().BeTrue();
		result.TargetPath.Should().BeNull();
		p.Fs.AllFiles.Should().NotContain(f => f.Contains(".target.", StringComparison.Ordinal));
	}

	[Test]
	public void AnUnexplainedDifferenceFailsWithTheRuleThatWouldExplainIt()
	{
		var p = Project();
		p.Engine.SeedBaseline(Catalog(false), "responses", HttpFile(p), null);

		var result = Compare(p, Catalog(true));

		result.Passed.Should().BeFalse();
		result.FailureMessage.Should().Be(
			"HTTP differs between baseline and target: tests/Acme.Tests/http/shop.http\n" +
			"1 unexplained difference(s), baseline expected, target actual:\n" +
			"$[0].body.items[0].badge   unexpected in actual\n" +
			"Rules that would explain them (under these headings in a file the rules include, or with --record):\n" +
			"  ## GET {{host}}/api/catalog\n" +
			"  added body $.items[*].badge\n" +
			"Baseline: .artifacts/.snapshots/received/Acme.Tests/http/shop.baseline.responses\n" +
			"Target:   .artifacts/.snapshots/received/Acme.Tests/http/shop.target.responses\n" +
			"Rules:    tests/Acme.Tests/http/shop.differences (none yet)");
		p.Read(result.TargetPath!).Should().Be(Catalog(true), "the target is written for review");
		p.Fs.AllFiles.Should().NotContain(f => f.Contains(".received.", StringComparison.Ordinal), "so snap status shows nothing pending");
	}

	[Test]
	public void ARuleExplainsItAndTheNextPassingRunRemovesTheTargetFile()
	{
		var p = Project();
		p.Engine.SeedBaseline(Catalog(false), "responses", HttpFile(p), null);
		Compare(p, Catalog(true)).Passed.Should().BeFalse();
		p.Fs.AddFile(Rules(p), new MockFileData("## *catalog\nadded body $.items[*].badge\n"));

		var result = Compare(p, Catalog(true));

		result.Passed.Should().BeTrue();
		result.Application.Explained.Should().ContainSingle();
		p.Fs.AllFiles.Should().NotContain(f => f.Contains(".target.", StringComparison.Ordinal));
	}

	[Test]
	public void AStaleRuleFailsEvenWhenEverythingIsExplained()
	{
		var p = Project("## *catalog\nadded body $.items[*].badge\nremoved body $.items[*].legacy\n");
		p.Engine.SeedBaseline(Catalog(false), "responses", HttpFile(p), null);

		var result = Compare(p, Catalog(true));

		result.Passed.Should().BeFalse();
		result.FailureMessage.Should().Contain("1 rule(s) matched nothing, so they are stale:\n  tests/Acme.Tests/http/shop.differences:3: removed body $.items[*].legacy")
			.And.NotContain("unexplained");
	}

	[Test]
	public void ComparingWithoutABaselineSaysSo()
	{
		var p = Project();

		var act = () => Compare(p, Catalog(false));

		act.Should().Throw<InvalidOperationException>().WithMessage("There is no baseline for tests/Acme.Tests/http/shop.http: run it against the baseline site first*");
	}

	[Test]
	public void ARulesFileThatDoesNotParseIsAnExceptionNotAFailure()
	{
		var p = Project("frobnicate\n");
		p.Engine.SeedBaseline(Catalog(false), "responses", HttpFile(p), null);

		var act = () => Compare(p, Catalog(false));

		act.Should().Throw<ExpectedDifferencesException>().Which.Errors.Should().ContainSingle().Which.Should().Contain("shop.differences:1: 'frobnicate' is not a verb");
	}

	[Test]
	public void RecordingWritesToTheIncludedFileAndTheNextRunPassesWithoutIt()
	{
		var p = Project("include? known/shop.differences\n");
		p.Engine.SeedBaseline(Catalog(false), "responses", HttpFile(p), null);
		var known = p.Fs.Path.Combine(p.Project.Directory, "http", "known", "shop.differences");

		var recorded = Compare(p, Catalog(true), new SiteComparisonOptions { Record = known, Note = "recorded against https://preview.example.com" });

		recorded.Passed.Should().BeTrue("a recorded difference is not a failure");
		recorded.Recorded.Should().Equal("added body $.items[*].badge");
		p.Read(known).Should().Be("# recorded against https://preview.example.com\n## GET {{host}}/api/catalog\nadded body $.items[*].badge\n");
		Compare(p, Catalog(true)).Passed.Should().BeTrue("the recorded rule explains it, and is used, so it is not stale");
	}

	[Test]
	public void RecordingIntoAFileNothingIncludesIsRefused()
	{
		var p = Project();
		p.Engine.SeedBaseline(Catalog(false), "responses", HttpFile(p), null);

		var act = () => Compare(p, Catalog(true), new SiteComparisonOptions { Record = p.Fs.Path.Combine(p.Project.Directory, "http", "known.differences") });

		act.Should().Throw<InvalidOperationException>().WithMessage("*is not an 'include?' of the rules*");
		p.Fs.FileExists(p.Fs.Path.Combine(p.Project.Directory, "http", "known.differences")).Should().BeFalse();
	}

	[Test]
	public void PruningRemovesTheRulesOfAShippedChange()
	{
		var p = Project("include? known.differences\n", ("http/known.differences", "## *catalog\nadded body $.items[*].badge\n"));
		p.Engine.SeedBaseline(Catalog(false), "responses", HttpFile(p), null);
		var known = p.Fs.Path.Combine(p.Project.Directory, "http", "known.differences");

		var result = Compare(p, Catalog(false), new SiteComparisonOptions { Prune = known });

		result.Passed.Should().BeTrue();
		result.Pruned.Should().Be(1);
		p.Read(known).Should().BeEmpty("the change shipped, so its rule and its section are gone");
	}

	// The CLI, with a fake runner that answers by the site it is asked for

	private sealed class CompareRun
	{
		public TestProject P { get; } = Project();
		public StringWriter Out { get; } = new();
		public StringWriter Err { get; } = new();
		public List<(string Host, TranscriptMode Mode, string? Environment)> Ran { get; } = [];
		public Func<string, string> Text { get; set; } = host => Catalog(host.Contains("preview", StringComparison.Ordinal));

		public Task<int> Invoke(
			string? baseline = "http://prod.test/", string? target = "http://preview.test/", bool values = false, bool reuse = false,
			string? record = null, string? prune = null, string[]? files = null, string? baselineEnv = null, string? targetEnv = null
		) =>
			new HttpCli(P.Fs, P.Project.Directory, Out, Err, (file, options, _) =>
			{
				var host = options.Variables["host"];
				Ran.Add((host, options.Transcript, options.Environment));
				return Task.FromResult(new HttpTranscript(file, Text(host), null));
			}, _ => null).CompareAsync(files ?? ["http/shop.http"], baseline, target, CancellationToken.None, baselineEnv, targetEnv, null, null, "host", values, reuse, record, prune);

		public string Output => Out.ToString().Replace("\r\n", "\n", StringComparison.Ordinal);

		public string Error => Err.ToString().Replace("\r\n", "\n", StringComparison.Ordinal);
	}

	[Test]
	public async Task BothSitesAreRunAsShapesByDefaultAndByValuesOnRequest()
	{
		var run = new CompareRun();

		(await run.Invoke(baselineEnv: "prod", targetEnv: "edge")).Should().Be(1);
		(await run.Invoke(values: true)).Should().Be(1);

		run.Ran.Should().Equal(
			("http://prod.test", TranscriptMode.Shape, "prod"), ("http://preview.test", TranscriptMode.Shape, "edge"),
			("http://prod.test", TranscriptMode.Compare, null), ("http://preview.test", TranscriptMode.Compare, null));
	}

	[Test]
	public async Task AnUnexplainedDifferenceExitsOneAndAnExplainedOneExitsZero()
	{
		var run = new CompareRun();

		(await run.Invoke()).Should().Be(1);
		run.Output.Should().Contain("1 unexplained difference(s)").And.Contain("added body $.items[*].badge");

		run.P.Fs.AddFile(Rules(run.P), new MockFileData("## *catalog\nadded body $.items[*].badge\n"));
		var passing = new CompareRun();
		passing.P.Fs.AddFile(Rules(passing.P), new MockFileData("## *catalog\nadded body $.items[*].badge\n"));
		(await passing.Invoke()).Should().Be(0);
		passing.Output.Should().Be("ok   http/shop.http (1 expected difference(s))\n");
	}

	[Test]
	public async Task ReusingTheBaselineSendsToTheBaselineSiteOnlyOnce()
	{
		var run = new CompareRun();
		await run.Invoke();

		await run.Invoke(reuse: true);

		run.Ran.Select(r => r.Host).Should().Equal("http://prod.test", "http://preview.test", "http://preview.test");
	}

	[Test]
	public async Task RecordThenPassesWithoutIt()
	{
		var run = new CompareRun();
		run.P.Fs.AddFile(Rules(run.P), new MockFileData("include? known/shop.differences\n"));

		(await run.Invoke(record: "http/known/shop.differences")).Should().Be(0);
		(await run.Invoke(reuse: true)).Should().Be(0);

		run.Output.Should().StartWith("Recorded 1 rule(s) for http/shop.http in http/known/shop.differences.\nok   http/shop.http (0 expected difference(s))\nok   http/shop.http (1 expected difference(s))");
	}

	[Test]
	public async Task BadInputExitsTwoWithTheProblem()
	{
		var run = new CompareRun();

		(await run.Invoke(baseline: null)).Should().Be(2);
		(await run.Invoke(target: "ftp://x")).Should().Be(2);
		(await run.Invoke(files: ["http/nope.http"])).Should().Be(2);
		run.P.Fs.AddFile(Rules(run.P), new MockFileData("nonsense\n"));
		(await run.Invoke()).Should().Be(2);
		(await run.Invoke(record: "http/other.differences")).Should().Be(2);

		run.Error.Should().Contain("Error: --baseline is required").And.Contain("--target 'ftp://x' is not an absolute http, https or file URL")
			.And.Contain("no .http file matches 'http/nope.http'").And.Contain("'nonsense' is not a verb");
	}

	// Two real servers

	private static readonly Lazy<TestServer> Preview = new(() =>
	{
		var server = new TestServer(preview: true);
		AppDomain.CurrentDomain.ProcessExit += (_, _) => server.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(5));
		return server;
	}, LazyThreadSafetyMode.ExecutionAndPublication);

	private static Task<SiteComparisonResult> Real(string file, bool values = false) =>
		HttpFileSnapshot.CompareAsync($"compare-http/{file}.http", new HttpCompareOptions
		{
			Baseline = new Uri(TestServer.Shared.Host),
			Target = new Uri(Preview.Value.Host),
			Values = values,
		});

	[Test]
	public async Task TheShapeOfAPreviewDiffersOnlyByTheAddedProperty()
	{
		var result = await Real("catalog");

		result.Passed.Should().BeTrue("{0}", result.FailureMessage);
		result.Application.Explained.Should().ContainSingle().Which.Difference.Path.Should().Be("$[0].body.items[0].badge");
	}

	[Test]
	public async Task WithoutARuleTheAddedPropertyFails()
	{
		var result = await Real("catalog-norule");

		result.Passed.Should().BeFalse();
		result.FailureMessage.Should().Contain("added body $.items[*].badge").And.Contain("Rules:    tests/Nullean.Snapshots.Http.Tests/compare-http/catalog-norule.differences (none yet)");
	}

	[Test]
	public async Task ValuesCompareOnceEachSitesOriginIsScrubbed()
	{
		var result = await Real("catalog-values", values: true);

		result.Passed.Should().BeTrue("{0}", result.FailureMessage);
		result.Application.Explained.Should().HaveCount(4, "a badge, two prices and the home link; the self link is equal once both origins are {{origin}}");
		TestContext.Current!.Output.WriteLine(result.BaselinePath);
		File.ReadAllText(result.BaselinePath).Should().Contain("\"self\": \"{origin}/api/catalog\"").And.NotContain(TestServer.Shared.Host);
	}

	[Test]
	public async Task ARuleThatMatchesNothingFailsTheRealRunToo()
	{
		var result = await Real("catalog-stale");

		result.Passed.Should().BeFalse();
		result.FailureMessage.Should().Contain("matched nothing, so they are stale").And.Contain("catalog-stale.differences:3: removed body $.items[*].legacy");
	}
}
