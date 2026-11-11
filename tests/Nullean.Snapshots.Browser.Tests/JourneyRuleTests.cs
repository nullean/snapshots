using System.IO.Abstractions.TestingHelpers;
using AwesomeAssertions;
using CsCheck;
using Nullean.Snapshots.Differences;
using Nullean.Snapshots.Tests;
using Nullean.Snapshots.Trees;

namespace Nullean.Snapshots.Browser.Tests;

/// <summary>Expected differences between two journey transcripts: rules over accessibility selectors, urls and read text.</summary>
public class JourneyRuleTests
{
	private static string Root => TestProject.Root;

	private const string Baseline =
		"""
		## snapshot -c -s nav
		url: {origin}/index.html
		- navigation "Main"
		  - link "Docs"
		  - link "Legacy page"
		  - heading "Elastic Docs" [level=1]
		- main
		  - link "Deep"

		## get text #version
		url: {origin}/index.html
		> Version 9.1.0

		""";

	private static string Edit(string text, string from, string to) => text.Replace(from, to, StringComparison.Ordinal);

	private static RuleApplication Apply(string baseline, string actual, string rules)
	{
		var fs = new MockFileSystem();
		fs.AddDirectory(fs.Path.Combine(Root, ".git"));
		fs.AddFile(fs.Path.Combine(Root, "shop.differences"), new MockFileData(rules));
		var set = RuleLoader.Load(fs, Root, fs.Path.Combine(Root, "shop.differences"));
		var b = JourneySnapshot.Format.Parse(baseline);
		var a = JourneySnapshot.Format.Parse(actual);
		return ExpectedDifferences.Apply(
			TreeDiffer.Instance.Diff(b, a),
			b,
			a,
			ExpectedDifferences.Bind(set, JourneyRuleTargets.Instance),
			JourneyRuleTargets.Instance
		);
	}

	private static readonly string WithNews = Edit(Baseline, "  - link \"Docs\"\n", "  - link \"Docs\"\n  - link \"What's new\"\n");

	[Test]
	public void AnAddedNodeIsExplainedByItsRoleAndName()
	{
		Apply(Baseline, WithNews, "## snapshot*\nadded link \"What's new\"\n").IsClean.Should().BeTrue();
		Apply(Baseline, WithNews, "## snapshot*\nadded link \"What*\"\n").IsClean.Should().BeTrue();
		Apply(Baseline, WithNews, "## snapshot*\nadded link /^What/\n").IsClean.Should().BeTrue();
		Apply(Baseline, WithNews, "## snapshot*\nadded link \"Other\"\n").Unexplained.Should().ContainSingle();
		Apply(Baseline, WithNews, "## snapshot*\nadded button \"What's new\"\n").Unexplained.Should().ContainSingle();
	}

	[Test]
	public void TheSuggestionIsTheRule()
	{
		var app = Apply(Baseline, WithNews, string.Empty);

		ExpectedDifferences.Suggest(app.UnexplainedFacts.Single(), JourneyRuleTargets.Instance).Should().Equal("added link \"What's new\"");
		app.UnexplainedFacts.Single().Heading.Should().Be("snapshot -c -s nav");
	}

	[Test]
	public void ARemovedNodeNeedsARemovedRule()
	{
		var actual = Edit(Baseline, "  - link \"Legacy page\"\n", string.Empty);

		Apply(Baseline, actual, "## snapshot*\nremoved link \"Legacy*\"\n").IsClean.Should().BeTrue();
		Apply(Baseline, actual, "## snapshot*\nadded link \"Legacy*\"\n").Unexplained.Should().ContainSingle();
	}

	[Test]
	public void SeveralTermsMeanInsideLikeCssDescendants()
	{
		Apply(Baseline, WithNews, "## snapshot*\nadded navigation link \"What's new\"\n").IsClean.Should().BeTrue();
		Apply(Baseline, WithNews, "## snapshot*\nadded navigation \"Main\" link\n").IsClean.Should().BeTrue();
		Apply(Baseline, WithNews, "## snapshot*\nadded main link\n").Unexplained.Should().ContainSingle("the link is in the navigation, not in main");
		Apply(Baseline, WithNews, "## snapshot*\nadded navigation\n")
			.Unexplained
			.Should()
			.ContainSingle("the added node is the link, not the navigation around it");
	}

	[Test]
	public void AChangedNameNeedsItsOldAndNewValues()
	{
		var actual = Edit(Baseline, "\"Elastic Docs\"", "\"Elastic Documentation\"");

		Apply(Baseline, actual, "## snapshot*\nchanged heading \"Elastic Docs\" -> \"Elastic Documentation\"\n").IsClean.Should().BeTrue();
		Apply(Baseline, actual, "## snapshot*\nchanged heading [level=1] \"Elastic Docs\" -> \"Elastic Documentation\"\n").IsClean.Should().BeTrue();
		Apply(Baseline, actual, "## snapshot*\nchanged heading [level=2] \"Elastic Docs\" -> \"Elastic Documentation\"\n").Unexplained.Should().ContainSingle();
		Apply(Baseline, actual, "## snapshot*\nchanged heading \"Elastic Docs\" -> \"Other\"\n").Unexplained.Should().ContainSingle();
		Apply(Baseline, actual, "## snapshot*\nchanged link \"Elastic Docs\" -> \"Elastic Documentation\"\n").Unexplained.Should().ContainSingle();
		Apply(Baseline, actual, "## snapshot*\nchanged heading\n").IsClean.Should().BeTrue("without values any change of the heading is expected");
	}

	[Test]
	public void AChangedAttributeIsMatchedByItsValues()
	{
		var actual = Edit(Baseline, "[level=1]", "[level=2]");

		Apply(Baseline, actual, "## snapshot*\nchanged heading [level] 1 -> 2\n").IsClean.Should().BeTrue();
		Apply(Baseline, actual, "## snapshot*\nchanged heading 1 -> 3\n").Unexplained.Should().ContainSingle();
	}

	[Test]
	public void AnAttributeThatAppearsOrGoesIsNamedInBrackets()
	{
		var actual = Edit(Baseline, "[level=1]", "[level=1, selected]");

		Apply(Baseline, actual, "## snapshot*\nadded heading [selected]\n").IsClean.Should().BeTrue();
		Apply(Baseline, actual, "## snapshot*\nadded heading\n").Unexplained.Should().ContainSingle("the heading itself did not appear");
		Apply(Baseline, actual, "## snapshot*\nadded heading [expanded]\n").Unexplained.Should().ContainSingle();
		Apply(actual, Baseline, "## snapshot*\nremoved heading [selected]\n").IsClean.Should().BeTrue();
	}

	[Test]
	public void IgnoreCoversTheNodeAndEverythingInsideIt()
	{
		var actual = Edit(WithNews, "\"Legacy page\"", "\"Old page\"");

		Apply(Baseline, actual, "## snapshot*\nignore navigation\n").IsClean.Should().BeTrue();
		Apply(Baseline, actual, "## snapshot*\nignore main\n").Unexplained.Should().HaveCount(2);
		Apply(Baseline, actual, "## snapshot*\nignore link\n").IsClean.Should().BeTrue();
		Apply(Baseline, actual, "## snapshot*\nignore link \"What*\"\n").Unexplained.Should().ContainSingle();
	}

	[Test]
	public void ANodeThatGainsItsFirstChildrenIsOneRulePerChild()
	{
		var actual = Edit(Baseline, "- main\n  - link \"Deep\"", "- main\n  - link \"Deep\"\n- complementary\n  - link \"A\"\n  - link \"B\"");
		var before = Edit(Baseline, "  - link \"Deep\"\n", string.Empty);
		var after = Edit(before, "- main\n", "- main\n  - link \"Deep\"\n");

		var app = Apply(before, after, string.Empty);

		ExpectedDifferences.Suggest(app.UnexplainedFacts.Single(), JourneyRuleTargets.Instance).Should().Equal("added link \"Deep\"");
		Apply(before, after, "## snapshot*\nadded link \"Deep\"\n").IsClean.Should().BeTrue();
		Apply(Baseline, actual, string.Empty).Unexplained.Should().HaveCount(1);
	}

	[Test]
	public void UrlAndTextAreFieldsOfASection()
	{
		var actual = Edit(Edit(Baseline, "Version 9.1.0", "Version 9.2.0"), "{origin}/index.html\n> Version", "{origin}/start.html\n> Version");

		Apply(Baseline, actual, "## get text*\nignore url\nchanged text /9\\.\\d/ -> /9\\.\\d/\n").IsClean.Should().BeTrue();
		Apply(Baseline, actual, "## get text*\nignore url\nchanged text /8\\.\\d/ -> /9\\.\\d/\n").Unexplained.Should().ContainSingle();
		Apply(Baseline, actual, "## snapshot*\nignore url\n").Unexplained.Should().HaveCount(2);
	}

	[Test]
	public void AnAddedLineOfAReadsTextIsExcusedByNamingTheLine()
	{
		var baseline = "## network requests --failed\nurl: {origin}/\n> GET {origin}/a 200\n";
		var actual = "## network requests --failed\nurl: {origin}/\n> GET {origin}/a 200\n> GET {origin}/static-res/fonts/MierB-Demi.woff2 (Font) 404\n";

		Apply(baseline, actual, "## network requests*\nadded text /static-res\\/fonts\\//\n").IsClean.Should().BeTrue();
		Apply(baseline, actual, "## network requests*\nadded text \"GET {origin}/other\"\n").Unexplained.Should().ContainSingle();
		var app = Apply(baseline, actual, string.Empty);
		ExpectedDifferences
			.Suggest(app.UnexplainedFacts.Single(), JourneyRuleTargets.Instance)
			.Should()
			.Equal("added text \"GET {origin}/static-res/fonts/MierB-Demi.woff2 (Font) 404\"");
	}

	[Test]
	public void TextWithANameIsStillAnAccessibilityNode()
	{
		var baseline = "## snapshot -c\nurl: {origin}/\n- main\n";
		var actual = "## snapshot -c\nurl: {origin}/\n- main\n  - text \"hello\"\n";

		Apply(baseline, actual, "## snapshot*\nadded text \"hello\"\n").IsClean.Should().BeTrue();
	}

	[Test]
	public void ABareRuleExplainsAWholeSection()
	{
		var actual = Baseline.Split("\n## get text")[0] + "\n";

		Apply(Baseline, actual, "## get text*\nremoved\n").IsClean.Should().BeTrue();
		Apply(Baseline, actual, "## get text*\nadded\n").Unexplained.Should().ContainSingle();
	}

	[Test]
	[Arguments("added \"Name\"", "is a name; it follows a role")]
	[Arguments("added link \"a\" \"b\"", "is a name; it follows a role, once")]
	[Arguments("added [level=1]", "filters a role")]
	[Arguments("added link [level", "is not [attribute] or [attribute=value]")]
	[Arguments("added link /(/", "is not a regular expression")]
	public void ABadTargetIsAnErrorWithItsLine(string rule, string message)
	{
		var fs = new MockFileSystem();
		fs.AddDirectory(fs.Path.Combine(Root, ".git"));
		fs.AddFile(fs.Path.Combine(Root, "shop.differences"), new MockFileData("## s\n" + rule + "\n"));
		var set = RuleLoader.Load(fs, Root, fs.Path.Combine(Root, "shop.differences"));

		((Action)(() => ExpectedDifferences.Bind(set, JourneyRuleTargets.Instance)))
			.Should()
			.Throw<ExpectedDifferencesException>()
			.Which
			.Errors
			.Should()
			.ContainSingle()
			.Which
			.Should()
			.Contain("shop.differences:2:")
			.And
			.Contain(message);
	}

	// Properties

	private static readonly Gen<Node> Aria = Gen.Recursive<Node>((depth, inner) =>
	{
		var role = Gen.OneOf("link", "heading", "list", "button");
		var name = Gen.OneOf("a", "b", "c d", "x\"y", "*", "");
		var attrs = Gen
			.Select(Gen.OneOf("level", "selected"), Gen.OneOf((Node)ScalarNode.Bool(true), ScalarNode.String("1"), ScalarNode.String("2")))
			.Array[0, 2]
			.Select(e => e.DistinctBy(x => x.Item1).ToArray());
		return Gen.Select(role, name.Null(), attrs, depth >= 3 ? Gen.Const(Array.Empty<Node>()) : inner.Array[0, 3]).Select((r, n, a, c) =>
		{
			var entries = new List<KeyValuePair<ScalarNode, Node>> { new(ScalarNode.String("role"), ScalarNode.String(r)) };
			if (n is not null)
				entries.Add(new(ScalarNode.String("name"), ScalarNode.String(n)));
			if (a.Length > 0)
				entries.Add(new(
					ScalarNode.String("attrs"),
					new MapNode([.. a.Select(x => new KeyValuePair<ScalarNode, Node>(ScalarNode.String(x.Item1), x.Item2))])
				));
			if (c.Length > 0)
				entries.Add(new(ScalarNode.String("children"), new SeqNode(c)));
			return (Node)new MapNode(entries);
		});
	});

	private static SeqNode Page(Node[] roots) =>
		new([
			new MapNode([
				new(ScalarNode.String("step"), ScalarNode.String("snapshot")),
				new(ScalarNode.String("url"), ScalarNode.String("{origin}/")),
				new(ScalarNode.String("tree"), new SeqNode(roots)),
			])
		]);

	[Test]
	public void ASuggestedRuleExplainsItsDifference() =>
		Gen.Select(Aria.Array[0, 3], Aria.Array[0, 3]).Sample(
			pair =>
			{
				var baseline = Page(pair.Item1);
				var actual = Page(pair.Item2);
				foreach (var difference in TreeDiffer.Instance.Diff(baseline, actual))
				{
					var facts = DifferenceFacts.Create(difference, baseline, actual, "step");
					var lines = ExpectedDifferences.Suggest(facts, JourneyRuleTargets.Instance);
					var fs = new MockFileSystem();
					fs.AddDirectory(fs.Path.Combine(Root, ".git"));
					fs.AddFile(fs.Path.Combine(Root, "shop.differences"), new MockFileData("## " + facts.Heading + "\n" + string.Join('\n', lines) + "\n"));
					var set = RuleLoader.Load(fs, Root, fs.Path.Combine(Root, "shop.differences"));
					var app = ExpectedDifferences.Apply(
						[difference],
						baseline,
						actual,
						ExpectedDifferences.Bind(set, JourneyRuleTargets.Instance),
						JourneyRuleTargets.Instance
					);
					if (lines.Count == 0 || !app.IsClean)
						return false;
				}

				return true;
			},
			print: pair => $"{AriaTreeText(pair.Item1)}\n---\n{AriaTreeText(pair.Item2)}"
		);

	private static string AriaTreeText(Node[] roots) => AriaTree.Print(new SeqNode(roots));
}
