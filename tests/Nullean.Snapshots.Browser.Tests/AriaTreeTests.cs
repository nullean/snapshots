using AwesomeAssertions;
using CsCheck;
using Nullean.Snapshots.Trees;

namespace Nullean.Snapshots.Browser.Tests;

public class AriaTreeTests
{
	private static readonly Gen<string> Names = Gen.String[Gen.Char["ab \"\\[]:,\n\t=-é"], 0, 6];

	private static readonly Gen<KeyValuePair<ScalarNode, Node>[]> Attrs = Gen.Select(
		Gen.Bool,
		Gen.Int[1, 3],
		Gen.OneOf("true", "false", "mixed"),
		Gen.Bool,
		Gen.Bool
	).Select((level, n, check, selected, disabled) =>
	{
		var attrs = new List<KeyValuePair<ScalarNode, Node>>();
		if (level)
			attrs.Add(new(ScalarNode.String("level"), ScalarNode.String(n.ToString(System.Globalization.CultureInfo.InvariantCulture))));
		if (check != "false")
			attrs.Add(new(ScalarNode.String("checked"), ScalarNode.String(check)));
		if (selected)
			attrs.Add(new(ScalarNode.String("selected"), ScalarNode.Bool(true)));
		if (disabled)
			attrs.Add(new(ScalarNode.String("disabled"), ScalarNode.Bool(true)));
		return attrs.ToArray();
	});

	private static Gen<SeqNode> Trees(int depth) =>
		Gen
			.Select(
				Gen.OneOf("button", "link", "heading", "StaticText", "main", "listitem"),
				Names,
				Gen.Bool,
				Attrs,
				Gen.String[Gen.Char["ab :[]\"#"], 0, 5],
				Gen.Bool,
				depth == 0 ? Gen.Const(new SeqNode([])) : Gen.Int[0, 2].SelectMany(_ => Trees(depth - 1))
			)
			.Select((role, name, hasName, attrs, value, hasValue, children) =>
			{
				var entries = new List<KeyValuePair<ScalarNode, Node>> { new(ScalarNode.String("role"), ScalarNode.String(role)) };
				if (hasName)
					entries.Add(new(ScalarNode.String("name"), ScalarNode.String(name)));
				if (attrs.Length > 0)
					entries.Add(new(ScalarNode.String("attrs"), new MapNode(attrs)));
				if (hasValue)
					entries.Add(new(ScalarNode.String("value"), ScalarNode.String(value)));
				if (children.Items.Count > 0)
					entries.Add(new(ScalarNode.String("children"), children));
				return (Node)new MapNode(entries);
			})
			.Array[0, 3]
			.Select(items => new SeqNode(items));

	[Test]
	public void PrintThenParseGivesTheTreeBack() =>
		Trees(2).Sample(tree =>
		{
			var printed = AriaTree.Print(tree);
			var parsed = AriaTree.Parse(printed);
			TreeDiffer.Instance.Diff(tree, parsed, TreeDiffOptions.Default).Should().BeEmpty(printed);
			AriaTree.Print(parsed).Should().Be(printed);
		});

	[Test]
	public void ParsesWhatNulleanCdpPrints()
	{
		var tree = AriaTree.Parse(
			"""
			- navigation "Main"
			  - link "Home"
			- main
			  - heading "Say \"hi\" \\ now\n" [level=1]
			  - checkbox "Only in stock" [checked=true, disabled]
			  - combobox "Sort" [expanded=false]: Name
			  - textbox: free text: with colon
			"""
		);

		AriaTree
			.Print(tree)
			.Should()
			.Be(
				"""
			- navigation "Main"
			  - link "Home"
			- main
			  - heading "Say \"hi\" \\ now\n" [level=1]
			  - checkbox "Only in stock" [checked=true, disabled]
			  - combobox "Sort" [expanded=false]: Name
			  - textbox: free text: with colon

			""".Replace(
					"\r\n",
					"\n",
					StringComparison.Ordinal
				)
			);
		var heading = (MapNode)((SeqNode)((MapNode)tree.Items[1]).Entries.Single(e => e.Key.Canonical == "children").Value).Items[0];
		((ScalarNode)heading.Entries.Single(e => e.Key.Canonical == "name").Value).Canonical.Should().Be("Say \"hi\" \\ now\n");
		heading.Span.Should().Be(new SourceSpan(4, 3));
	}

	[Test]
	public void AnEmptyTreeIsAnEmptySequence() => AriaTree.Parse("").Items.Should().BeEmpty();

	[Test]
	[Arguments("  - button", "Line 1: indented 2 spaces, which is not a level below the line before.")]
	[Arguments("- main\n      - button", "Line 2: indented 6 spaces, which is not a level below the line before.")]
	[Arguments("- main\n button", "Line 2: indented 1 spaces, which is not a level below the line before.")]
	[Arguments("button", "Line 1: an accessibility tree line starts with '- '.")]
	[Arguments("- button \"open", "Line 1: the name's quote is not closed.")]
	[Arguments("- button [level=1", "Line 1: the attribute list is not closed.")]
	[Arguments("- button \"Go\" trailing", "Line 1: unexpected text after the node: ' trailing'.")]
	[Arguments("- ", "Line 1: the node has no role.")]
	public void RejectsWhatIsNotATree(string text, string message) =>
		FluentActions.Invoking(() => AriaTree.Parse(text)).Should().Throw<FormatException>().WithMessage(message);
}
