using System.IO.Abstractions.TestingHelpers;
using AwesomeAssertions;
using CsCheck;
using Nullean.Snapshots.Differences;
using Nullean.Snapshots.Tests;
using Nullean.Snapshots.Trees;

namespace Nullean.Snapshots.Http.Tests;

/// <summary>Expected differences between two <c>.http</c> transcripts: rules over status, headers and body paths.</summary>
public class HttpRuleTests
{
	private static string Root => TestProject.Root;

	private const string Baseline = """
		## search: GET {{host}}/api/search
		status: 200 OK
		content-type: application/json
		x-legacy: 1

		{
		  "hits": [
		    {
		      "id": 1,
		      "title": "a"
		    }
		  ],
		  "meta": {
		    "took": 5
		  },
		  "total": 1
		}

		## GET {{host}}/api/health
		status: 200 OK

		""";

	private static string Edit(string text, string from, string to) => text.Replace(from, to, StringComparison.Ordinal);

	private static MockFileSystem Repo(params (string Path, string Text)[] files)
	{
		var fs = new MockFileSystem();
		fs.AddDirectory(fs.Path.Combine(Root, ".git"));
		foreach (var (path, text) in files)
			fs.AddFile(fs.Path.Combine(Root, path), new MockFileData(text));
		return fs;
	}

	private static string Sidecar(MockFileSystem fs) => fs.Path.Combine(Root, "shop.differences");

	private static RuleSet Load(MockFileSystem fs) => RuleLoader.Load(fs, Root, Sidecar(fs));

	private static RuleApplication Apply(string baseline, string actual, RuleSet rules)
	{
		var b = HttpSnapshot.Format.Parse(baseline);
		var a = HttpSnapshot.Format.Parse(actual);
		return ExpectedDifferences.Apply(TreeDiffer.Instance.Diff(b, a), b, a, ExpectedDifferences.Bind(rules, HttpRuleTargets.Instance), HttpRuleTargets.Instance);
	}

	private static RuleApplication Apply(string baseline, string actual, string rules) => Apply(baseline, actual, Load(Repo(("shop.differences", rules))));

	private static readonly string WithHighlight = Edit(Baseline, "\"title\": \"a\"", "\"highlight\": \"x\",\n      \"title\": \"a\"");

	[Test]
	public void AnAddedBodyPropertyIsExplainedByARuleForItsPath()
	{
		var app = Apply(Baseline, WithHighlight, "## search*\nadded body $.hits[*].highlight\n");

		app.IsClean.Should().BeTrue();
		app.Explained.Should().ContainSingle().Which.Rule.Where.Line.Should().Be(2);
	}

	[Test]
	public void WithoutARuleItIsUnexplainedAndTheSuggestionIsTheRule()
	{
		var app = Apply(Baseline, WithHighlight, string.Empty);

		app.Unexplained.Should().ContainSingle();
		app.UnexplainedFacts.Single().Heading.Should().Be("search: GET {{host}}/api/search");
		ExpectedDifferences.Suggest(app.UnexplainedFacts.Single(), HttpRuleTargets.Instance).Should().Equal("added body $.hits[*].highlight");
	}

	[Test]
	public void ARuleOnlyAppliesToItsSection() =>
		Apply(Baseline, WithHighlight, "## GET {{host}}/api/health\nadded body $.hits[*].highlight\n").Unexplained.Should().ContainSingle();

	[Test]
	public void ARuleBeforeAnySectionAppliesToEverySection() =>
		Apply(Baseline, WithHighlight, "added body $..highlight\n").IsClean.Should().BeTrue();

	[Test]
	public void APathMustMatchWhole() =>
		Apply(Baseline, WithHighlight, "## search*\nadded body $.hits[*]\n").Unexplained.Should().ContainSingle("a rule for the hits does not cover one of their properties");

	[Test]
	public void IgnoreCoversEverythingBelow()
	{
		var actual = Edit(Baseline, "\"took\": 5", "\"took\": \"fast\",\n    \"shards\": 3");

		Apply(Baseline, actual, "## search*\nignore body $.meta\n").IsClean.Should().BeTrue();
		Apply(Baseline, actual, "## search*\nignore body $.hits\n").Unexplained.Should().HaveCount(2);
	}

	[Test]
	public void AChangedStatusMustMatchOldAndNew()
	{
		var actual = Edit(Baseline, "## GET {{host}}/api/health\nstatus: 200 OK", "## GET {{host}}/api/health\nstatus: 404 Not Found");

		Apply(Baseline, actual, "## *health\nchanged status \"200 OK\" -> \"404 *\"\n").IsClean.Should().BeTrue();
		Apply(Baseline, actual, "## *health\nchanged status \"200 OK\" -> \"500 *\"\n").Unexplained.Should().ContainSingle();
		Apply(Baseline, actual, "## *health\nchanged status\n").IsClean.Should().BeTrue("without values any change of the status is expected");
	}

	[Test]
	public void ARegexMatchesAChangedValue()
	{
		var actual = Edit(Baseline, "\"took\": 5", "\"took\": 7");

		Apply(Baseline, actual, "## search*\nchanged body $.meta.took /^\\d$/ -> /^\\d$/\n").IsClean.Should().BeTrue();
	}

	[Test]
	public void HeadersAreMatchedByNameWhateverTheCase()
	{
		var dropped = Edit(Baseline, "x-legacy: 1\n", string.Empty);

		Apply(Baseline, dropped, "## search*\nremoved header X-LEGACY\n").IsClean.Should().BeTrue();
		Apply(Baseline, dropped, "## search*\nremoved header x-other\n").Unexplained.Should().ContainSingle();
		Apply(Baseline, dropped, "## search*\nremoved header x-*\n").IsClean.Should().BeTrue();
	}

	[Test]
	public void ABareAddedExplainsAWholeNewSection()
	{
		var actual = Baseline + "\n## GET {{host}}/api/orders\nstatus: 200 OK\n";

		Apply(Baseline, actual, "## *orders\nadded\n").IsClean.Should().BeTrue();
		Apply(Baseline, actual, "## *orders\nadded status\n").Unexplained.Should().ContainSingle();
	}

	[Test]
	public void ARuleThatMatchesNothingIsStaleUnlessItIsMaybe()
	{
		var rules = Load(Repo(("shop.differences", "## search*\nadded body $.hits[*].highlight\nremoved header x-gone\nmaybe removed header x-maybe\n")));

		var app = Apply(Baseline, WithHighlight, rules);

		app.Unexplained.Should().BeEmpty();
		app.Stale.Should().ContainSingle().Which.Text.Should().Be("removed header x-gone");
		app.IsClean.Should().BeFalse();
	}

	[Test]
	public void OnlyTheSidecarAndOptionalIncludesCanGoStale()
	{
		var rules = Load(Repo(
			("shop.differences", "include shared.differences\ninclude? known.differences\n"),
			("shared.differences", "## search*\nremoved header x-never\n"),
			("known.differences", "## search*\nremoved header x-never-either\n")));

		var app = Apply(Baseline, Baseline, rules);

		app.Stale.Should().ContainSingle().Which.Origin.Should().Be(RuleOrigin.Optional);
	}

	[Test]
	public void AFolderFileExplainsEveryFileBelowItAndNeverGoesStale()
	{
		var rules = Load(Repo((".differences", "ignore header x-legacy\nremoved header x-never\n"), ("shop.differences", "")));

		var app = Apply(Baseline, Edit(Baseline, "x-legacy: 1\n", string.Empty), rules);

		app.IsClean.Should().BeTrue("the folder file's rule explains the dropped header, and the one nothing needed is shared, not stale");
	}

	[Test]
	public void ATargetTheFormatDoesNotKnowIsAnErrorWithItsLine()
	{
		var rules = Load(Repo(("shop.differences", "## search*\nadded body $.a\nadded frobnicate\nignore header\nadded body $a\n")));

		var errors = ((Action)(() => ExpectedDifferences.Bind(rules, HttpRuleTargets.Instance))).Should().Throw<ExpectedDifferencesException>().Which.Errors;

		errors.Should().HaveCount(3);
		errors[0].Should().EndWith("shop.differences:3: 'frobnicate' is not a target: status, header NAME, body [PATH], text, binary or request.");
		errors[1].Should().Contain(":4:");
		errors[2].Should().Contain(":5:").And.Contain("Unexpected 'a' in path");
	}

	[Test]
	public void RecordingWritesRulesThatExplainTheNextRun()
	{
		var fs = Repo(("shop.differences", "include? known.differences\n"));
		var actual = Edit(WithHighlight, "x-legacy: 1\n", string.Empty);
		var first = Apply(Baseline, actual, Load(fs));
		first.Unexplained.Should().HaveCount(2);

		var written = ExpectedDifferences.Record(Load(fs), fs.Path.Combine(Root, "known.differences"), first.UnexplainedFacts, HttpRuleTargets.Instance, "recorded against https://preview.example.com");

		written.Should().Equal("removed header x-legacy", "added body $.hits[*].highlight");
		fs.File.ReadAllText(fs.Path.Combine(Root, "known.differences")).Should().Be("""
			# recorded against https://preview.example.com
			## search: GET {{host}}/api/search
			removed header x-legacy
			added body $.hits[*].highlight

			""".Replace("\r\n", "\n", StringComparison.Ordinal));
		Apply(Baseline, actual, Load(fs)).IsClean.Should().BeTrue("the recorded rules explain everything and none is stale");
		fs.AllFiles.Should().NotContain(f => f.EndsWith(".tmp", StringComparison.Ordinal));
	}

	[Test]
	public void RecordingAppendsAfterWhatIsThere()
	{
		var fs = Repo(("shop.differences", "include? known.differences\n"), ("known.differences", "## other\nignore status\n"));
		var app = Apply(Baseline, WithHighlight, Load(fs));

		ExpectedDifferences.Record(Load(fs), fs.Path.Combine(Root, "known.differences"), app.UnexplainedFacts, HttpRuleTargets.Instance, "later");

		fs.File.ReadAllText(fs.Path.Combine(Root, "known.differences")).Should().StartWith("## other\nignore status\n\n# later\n## search: GET {{host}}/api/search\n");
	}

	[Test]
	public void RecordingIsRefusedForAFileNothingIncludes()
	{
		var fs = Repo(("shop.differences", "include shared.differences\n"), ("shared.differences", string.Empty));
		var app = Apply(Baseline, WithHighlight, Load(fs));

		foreach (var file in new[] { "elsewhere.differences", "shared.differences", "shop.differences" })
		{
			var record = () => ExpectedDifferences.Record(Load(fs), fs.Path.Combine(Root, file), app.UnexplainedFacts, HttpRuleTargets.Instance, "x");
			record.Should().Throw<InvalidOperationException>().Which.Message.Should().Contain("is not an 'include?'");
		}

		fs.File.ReadAllText(fs.Path.Combine(Root, "shared.differences")).Should().BeEmpty("nothing else is ever written");
	}

	[Test]
	public void PruningRemovesStaleRulesAndTheSectionsTheyLeaveEmpty()
	{
		var fs = Repo(
			("shop.differences", "include? known.differences\n"),
			("known.differences", "# kept comment\n## search*\nadded body $.hits[*].highlight\nremoved header x-gone\n\n## *health\nremoved header x-gone\n"));
		var rules = Load(fs);
		var app = Apply(Baseline, WithHighlight, rules);

		var removed = ExpectedDifferences.Prune(rules, fs.Path.Combine(Root, "known.differences"), app);

		removed.Should().Be(2);
		fs.File.ReadAllText(fs.Path.Combine(Root, "known.differences")).Should().Be("# kept comment\n## search*\nadded body $.hits[*].highlight\n");
		Apply(Baseline, WithHighlight, Load(fs)).IsClean.Should().BeTrue();
	}

	// Properties

	private static readonly Gen<string> Key = Gen.OneOf("a", "b", "c", "d e", "x\"y", "ü", "#", "*");

	private static readonly Gen<Node> Value = Gen.Recursive<Node>((depth, inner) =>
	{
		var scalar = Gen.OneOf(
			Gen.Const((Node)ScalarNode.Null()),
			Gen.Bool.Select(b => (Node)ScalarNode.Bool(b)),
			Gen.Int[0, 9].Select(i => (Node)new ScalarNode(ScalarKind.Int, i.ToString(System.Globalization.CultureInfo.InvariantCulture))),
			Gen.OneOf("", "x", "a b", "a*b", "line\nbreak", "q\"uote", "back\\slash", "c\r\nd", "\t").Select(s => (Node)ScalarNode.String(s)));
		if (depth >= 3)
			return scalar;
		var map = Gen.Select(Key, inner).Array[0, 3].Select(e =>
		{
			var seen = new HashSet<string>(StringComparer.Ordinal);
			return (Node)new MapNode([.. e.Where(x => seen.Add(x.Item1)).Select(x => new KeyValuePair<ScalarNode, Node>(ScalarNode.String(x.Item1), x.Item2))]);
		});
		return Gen.Frequency((3, scalar), (2, map), (2, inner.Array[0, 3].Select(items => (Node)new SeqNode(items))));
	});

	private static Node Section(string heading, Node body) =>
		new MapNode([
			new(ScalarNode.String("request"), ScalarNode.String(heading)),
			new(ScalarNode.String("status"), ScalarNode.String("200 OK")),
			new(ScalarNode.String("body"), body),
		]);

	[Test]
	public void ASuggestedRuleExplainsItsDifference() =>
		Gen.Select(Value, Value).Sample(pair =>
		{
			Node baseline = new SeqNode([Section("GET {{host}}/x", pair.Item1)]);
			Node actual = new SeqNode([Section("GET {{host}}/x", pair.Item2)]);
			foreach (var difference in TreeDiffer.Instance.Diff(baseline, actual))
			{
				var facts = DifferenceFacts.Create(difference, baseline, actual, "request");
				var lines = ExpectedDifferences.Suggest(facts, HttpRuleTargets.Instance);
				var fs = Repo(("shop.differences", "## " + facts.Heading + "\n" + string.Join('\n', lines) + "\n"));
				var rules = Load(fs);
				var app = ExpectedDifferences.Apply([difference], baseline, actual, ExpectedDifferences.Bind(rules, HttpRuleTargets.Instance), HttpRuleTargets.Instance);
				if (lines.Count == 0 || !app.IsClean)
					return false;
			}

			return true;
		});
}
