using System.Globalization;
using AwesomeAssertions;
using CsCheck;
using Nullean.Snapshots.Trees;

namespace Nullean.Snapshots.Json.Tests;

/// <summary>The shape projection: values reduced to kinds, arrays collapsed to their distinct shapes.</summary>
public class TreeShapeTests
{
	private static readonly TreeDiffOptions Strict = new() { StrictNumbers = true };

	private static string Shape(string json) => JsonSnapshot.Pretty(TreeGen.ToJson(TreeShape.Project(JsonSnapshot.Parse(json))));

	[Test]
	public void ValuesBecomeTheirKind() =>
		Shape(/*lang=json,strict*/ """{"s":"hello","b":true,"i":42,"f":4.5,"e":1e3,"n":null}""")
			.Should()
			.Be(
				"""
			{
			  "b": false,
			  "e": 1.0,
			  "f": 1.0,
			  "i": 1,
			  "n": null,
			  "s": "string"
			}

			""".Replace(
					"\r\n",
					"\n",
					StringComparison.Ordinal
				)
			);

	[Test]
	public void KeysAreSortedSoServerOrderNeverShows() =>
		Shape(/*lang=json,strict*/ """{"b":1,"a":{"d":1,"c":1}}""").Should().Be(Shape(/*lang=json,strict*/ """{"a":{"c":2,"d":2},"b":2}"""));

	[Test]
	public void AnArrayCollapsesToTheUnionOfItsItems() =>
		Shape(/*lang=json,strict*/ """[{"id":1,"name":"a"},{"id":2,"extra":true},{"id":3,"name":null}]""")
			.Should()
			.Be(Shape(/*lang=json,strict*/ """[{"extra":false,"id":1,"name":"x"}]"""));

	[Test]
	public void TheNumberOfItemsNeverShows() =>
		Shape(/*lang=json,strict*/ """{"hits":[{"id":1},{"id":2},{"id":3}]}""").Should().Be(Shape(/*lang=json,strict*/ """{"hits":[{"id":9}]}"""));

	[Test]
	public void AnEmptyArrayStaysEmptySoAnEmptiedListIsADifference()
	{
		var filled = TreeShape.Project(JsonSnapshot.Parse(/*lang=json,strict*/ """{"hits":[{"id":1}]}"""));
		var emptied = TreeShape.Project(JsonSnapshot.Parse(/*lang=json,strict*/ """{"hits":[]}"""));

		TreeDiffer.Instance.Diff(filled, emptied).Should().NotBeEmpty();
	}

	[Test]
	public void ANullFieldTakesTheKindOfTheItemsThatHaveOne() =>
		Shape(/*lang=json,strict*/ """[{"a":null},{"a":"x"}]""").Should().Be(Shape(/*lang=json,strict*/ """[{"a":"y"}]"""));

	[Test]
	public void AFieldWithTwoKindsKeepsBothSoNeitherIsLost()
	{
		var shape = (SeqNode)TreeShape.Project(JsonSnapshot.Parse(/*lang=json,strict*/ """[{"a":"x"},{"a":1}]"""));

		shape.Items.Should().HaveCount(2);
	}

	[Test]
	public void ScalarsOfEachKindAppearOnceInAFixedOrder()
	{
		var shape = (SeqNode)TreeShape.Project(JsonSnapshot.Parse(/*lang=json,strict*/ """["b",2,"a",null,1.5,true,3,false]"""));

		shape
			.Items
			.Cast<ScalarNode>()
			.Select(s => s.Kind)
			.Should()
			.Equal(ScalarKind.Null, ScalarKind.Bool, ScalarKind.Int, ScalarKind.Float, ScalarKind.String);
	}

	[Test]
	public void NestedArraysMergeIntoOne() => Shape(/*lang=json,strict*/ """[[1],[2,3],["a"]]""").Should().Be(Shape(/*lang=json,strict*/ """[["a",1]]"""));

	[Test]
	public void AnAddedPropertyIsExactlyOneDifference()
	{
		var before = TreeShape.Project(JsonSnapshot.Parse(/*lang=json,strict*/ """{"hits":[{"id":1,"tags":["a"]}]}"""));
		var after = TreeShape.Project(JsonSnapshot.Parse(/*lang=json,strict*/ """{"hits":[{"id":1,"tags":["a"],"score":0.5}]}"""));

		TreeDiffer
			.Instance
			.Diff(before, after)
			.Should()
			.ContainSingle()
			.Which
			.Should()
			.Match<TreeDifference>(d => d.Kind == DifferenceKind.Unexpected && d.Path.EndsWith("score", StringComparison.Ordinal));
	}

	// Properties

	[Test]
	public void ProjectionIsIdempotent() =>
		TreeGen.Tree.Sample(t =>
		{
			var once = TreeShape.Project(t);
			return TreeDiffer.DeepEquals(once, TreeShape.Project(once), Strict);
		});

	[Test]
	public void ChangingValuesButNotKindsChangesNothing() =>
		TreeGen.Tree.Sample(t => TreeDiffer.DeepEquals(TreeShape.Project(t), TreeShape.Project(Revalue(t)), Strict));

	[Test]
	public void MapKeyOrderChangesNothing() =>
		TreeGen.Tree.Sample(t => TreeDiffer.DeepEquals(TreeShape.Project(t), TreeShape.Project(TreeGen.ReverseKeys(t)), Strict));

	[Test]
	public void AnAddedRootPropertyIsExactlyOneDifference() =>
		TreeGen.MapWith(0).Sample(m =>
		{
			var added = new MapNode([.. m.Entries, new(ScalarNode.String("zz_added"), ScalarNode.String("x"))]);
			var diffs = TreeDiffer.Instance.Diff(TreeShape.Project(m), TreeShape.Project(added), Strict);
			return diffs.Count == 1 && diffs[0].Kind == DifferenceKind.Unexpected;
		});

	/// <summary>The same tree with every value replaced by another of the same kind.</summary>
	private static Node Revalue(Node node) => node switch
	{
		MapNode m => new MapNode([.. m.Entries.Select(e => new KeyValuePair<ScalarNode, Node>(e.Key, Revalue(e.Value)))]),
		SeqNode s => new SeqNode([.. s.Items.Select(Revalue)]),
		ScalarNode { Kind: ScalarKind.String } s => ScalarNode.String(s.Canonical + "!"),
		ScalarNode { Kind: ScalarKind.Bool } s => ScalarNode.Bool(s.Canonical != "true"),
		ScalarNode { Kind: ScalarKind.Int } => new ScalarNode(ScalarKind.Int, 7.ToString(CultureInfo.InvariantCulture)),
		ScalarNode { Kind: ScalarKind.Float } => new ScalarNode(ScalarKind.Float, "2.5"),
		_ => node,
	};
}
