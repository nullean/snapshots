using AwesomeAssertions;
using CsCheck;
using Nullean.Cdp.Journeys;
using Nullean.Snapshots.Trees;

namespace Nullean.Snapshots.Browser.Tests;

public class JourneyTranscriptTests
{
	private static readonly Uri Base = new("http://localhost:5000/shop/");

	private static string Render(params Checkpoint[] checkpoints) =>
		JourneyTranscript.Render(checkpoints, Base);

	private static Checkpoint Read(string command, string output, string url = "http://localhost:5000/shop/index.html") => new(1, command, url, output);

	private const string Tree = "- main\n  - heading \"Shop\" [level=1]\n  - button \"Add\"";

	[Test]
	public void RendersSectionsWithScrubbedUrlsAndQuotedText() =>
		Render(
			Read("get text #count", "1 item\n\nfrom http://localhost:5000/api"),
			Read("snapshot -c -s main", Tree + "\n"),
			Read("get title", "")
		).Should().Be("""
			## get text #count
			url: {origin}/shop/index.html
			> 1 item
			>
			> from {origin}/api

			## snapshot -c -s main
			url: {origin}/shop/index.html
			- main
			  - heading "Shop" [level=1]
			  - button "Add"

			## get title
			url: {origin}/shop/index.html
			>

			""".Replace("\r\n", "\n", StringComparison.Ordinal));

	[Test]
	public void AFileBaseUrlScrubsItsWholeFolder()
	{
		var root = new Uri("file:///home/ci/work/tests/Shop.Tests/site/");
		var text = JourneyTranscript.Render([new Checkpoint(1, "get url", "file:///home/ci/work/tests/Shop.Tests/site/a.html", "file:///home/ci/work/tests/Shop.Tests/site/a.html")], root);
		text.Should().Be("## get url\nurl: {origin}/a.html\n> {origin}/a.html\n");
	}

	[Test]
	public void PageTextCannotStartASectionOrPassForATree()
	{
		var text = Render(Read("get text body", "## snapshot\nurl: evil\n- button \"Pay\""));
		var sections = JourneyTranscript.Parse(text).Items;

		sections.Should().HaveCount(1);
		((ScalarNode)((MapNode)sections[0]).Entries.Single(e => e.Key.Canonical == "text").Value).Canonical
			.Should().Be("## snapshot\nurl: evil\n- button \"Pay\"");
	}

	[Test]
	public void RenderThenParseKeepsEverySection() =>
		Gen.Select(Gen.OneOf("get text #a", "snapshot -c", "is visible #b", "get url"), Gen.String[Gen.Char["ab #>-\n\"[]"], 0, 8])
			.Array[0, 5]
			.Sample(steps =>
			{
				var checkpoints = steps.Select(s => s.Item1.StartsWith("snapshot", StringComparison.Ordinal)
					? Read(s.Item1, Tree)
					: Read(s.Item1, s.Item2)).ToArray();
				var sections = JourneyTranscript.Parse(Render(checkpoints)).Items.Cast<MapNode>().ToArray();

				sections.Should().HaveCount(checkpoints.Length);
				for (var i = 0; i < sections.Length; i++)
				{
					var fields = sections[i].Entries.ToDictionary(e => e.Key.Canonical, e => e.Value);
					((ScalarNode)fields["step"]).Canonical.Should().Be(checkpoints[i].Command);
					if (fields.TryGetValue("text", out var output))
						((ScalarNode)output).Canonical.Should().Be(checkpoints[i].Output);
					else
						AriaTree.Print((SeqNode)fields["tree"]).TrimEnd('\n').Should().Be(Tree);
				}
			});

	[Test]
	public void AnInsertedSectionIsOneDifference()
	{
		var verified = Render(Read("get title", "Shop"), Read("snapshot -c -s main", Tree));
		var actual = Render(Read("get title", "Shop"), Read("get text #count", "1"), Read("snapshot -c -s main", Tree));

		var comparison = JourneySnapshot.Format.Compare(verified, actual);

		comparison.Differences.Should().HaveCount(1);
		comparison.LineDiff.Should().NotBeNullOrEmpty();
	}

	[Test]
	public void AChangedNodeIsOneDifferenceAtItsPath()
	{
		var verified = Render(Read("snapshot -c -s main", Tree));
		var actual = Render(Read("snapshot -c -s main", Tree.Replace("\"Shop\"", "\"Store\"", StringComparison.Ordinal)));

		var comparison = JourneySnapshot.Format.Compare(verified, actual);

		SnapshotMessages.Describe(comparison).Should().MatchSnapshot();
	}

	[Test]
	public void AnInsertedListItemIsOneDifference()
	{
		var verified = AriaTree.Print(AriaTree.Parse("- list\n  - listitem \"a\"\n  - listitem \"c\""));
		var actual = AriaTree.Print(AriaTree.Parse("- list\n  - listitem \"a\"\n  - listitem \"b\"\n  - listitem \"c\""));

		JourneySnapshot.AriaFormat.Compare(verified, actual).Differences.Should().HaveCount(1);
	}

	[Test]
	public void MatchingTranscriptsMatch() =>
		JourneySnapshot.Format.Compare(Render(Read("snapshot", Tree)), Render(Read("snapshot", Tree))).IsMatch.Should().BeTrue();

	[Test]
	[Arguments("url: x", "Line 1: a section starts with '## ' and the step's command.")]
	[Arguments("## get url\n> x", "Line 2: the section's second line is 'url: ' and the page's URL.")]
	[Arguments("## get url\nurl: u\n> x\nnot quoted", "Line 4: every line of a read's text starts with '> '.")]
	[Arguments("## snapshot\nurl: u\nbutton", "Line 3: an accessibility tree line starts with '- '.")]
	public void RejectsWhatIsNotATranscript(string text, string message) =>
		FluentActions.Invoking(() => JourneyTranscript.Parse(text)).Should().Throw<FormatException>().WithMessage(message);

	[Test]
	public void AnInvalidVerifiedFileIsADifference() =>
		JourneySnapshot.Format.Compare("garbage", Render(Read("get title", "x"))).Differences.Single().Single()
			.Should().Be("The verified file is not valid Journey: Line 1: a section starts with '## ' and the step's command.");
}
