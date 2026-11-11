using System.Diagnostics;
using System.Text.Json.JsonDiffPatch;
using System.Text.Json.Nodes;
using AwesomeAssertions;
using CsCheck;
using Nullean.Snapshots.Trees;

namespace Nullean.Snapshots.Json.Tests;

/// <summary>The tree differ's properties, numbered as in ROADMAP.md.</summary>
public partial class TreeDifferProperties
{
	private static readonly TreeDiffer Differ = TreeDiffer.Instance;

	private static IReadOnlyList<TreeDifference> Diff(Node e, Node a, TreeDiffOptions? o = null) => Differ.Diff(e, a, o);

	/// <summary>A tree and a variation of it: itself, a key-reversed copy, a JSON round trip, or an unrelated tree.</summary>
	private static readonly Gen<(Node A, Node B)> Pair = Gen.OneOf(
		TreeGen.Tree.Select(t => (t, t)),
		TreeGen.Tree.Select(t => (t, TreeGen.ReverseKeys(t))),
		TreeGen.Tree.Select(t => (t, JsonSnapshot.Parse(TreeGen.ToJson(t)))),
		Gen.Select(TreeGen.Tree, TreeGen.Tree),
		Gen.Select(TreeGen.MapWith(3), Gen.Int[0, 2]).Select((m, i) => ((Node)m, (Node)new MapNode([.. m.Entries.Where((_, k) => k != i)]))));

	// 1
	[Test]
	public void Reflexive() => TreeGen.Tree.Sample(t => Diff(t, t).Count == 0);

	// 2: equality agrees with an independent implementation. Not for numbers past double precision, which the oracle
	// compares as doubles.
	[Test]
	public void AgreesWithTheJsonDiffPatchOracle() =>
		Pair.Where(p => !HasHugeNumbers(p.A) && !HasHugeNumbers(p.B)).Sample(p =>
		{
			var ours = Diff(p.A, p.B).Count == 0;
			var oracle = JsonDiffPatcher.DeepEquals(JsonNode.Parse(TreeGen.ToJson(p.A)), JsonNode.Parse(TreeGen.ToJson(p.B)), JsonElementComparison.Semantic);
			return ours == oracle;
		}, print: Show);

	// 3: emptiness is symmetric, and swapping sides swaps missing for unexpected.
	[Test]
	public void SwappingSidesSwapsMissingAndUnexpected() =>
		Pair.Sample(p =>
		{
			var forward = Diff(p.A, p.B);
			var backward = Diff(p.B, p.A);
			int Count(IReadOnlyList<TreeDifference> d, DifferenceKind k) => d.Count(x => x.Kind == k);
			var forwardEmpty = forward.Count == 0;
			var backwardEmpty = backward.Count == 0;
			return forwardEmpty == backwardEmpty
				&& Count(forward, DifferenceKind.Missing) - Count(forward, DifferenceKind.Unexpected)
					== Count(backward, DifferenceKind.Unexpected) - Count(backward, DifferenceKind.Missing);
		});

	// 4: k edits on distinct keys of a map give exactly k differences, each at the edited key.
	[Test]
	public void MapEditsGiveOneDifferenceEach() =>
		Gen.Select(TreeGen.MapWith(4), Gen.Int[0, 2].Array[4, 4], TreeGen.Scalar).Sample((map, kinds, scalar) =>
		{
			var entries = map.Entries.ToList();
			var edited = new List<KeyValuePair<ScalarNode, Node>>();
			var expectedPaths = new HashSet<string>(StringComparer.Ordinal);
			for (var i = 0; i < entries.Count; i++)
			{
				var (key, value) = entries[i];
				var path = NodePath.Format([PathSegment.ForKey(key.Canonical)]);
				switch (i < kinds.Length ? kinds[i] : 9)
				{
					case 0: // remove the key
						expectedPaths.Add(path);
						break;
					case 1 when !TreeDiffer.DeepEquals(value, scalar): // replace the value with a different scalar
						edited.Add(new(key, scalar));
						expectedPaths.Add(path);
						break;
					case 2: // add a key next to it
						edited.Add(entries[i]);
						var added = ScalarNode.String("added_" + i);
						edited.Add(new(added, scalar));
						expectedPaths.Add(NodePath.Format([PathSegment.ForKey(added.Canonical)]));
						break;
					default:
						edited.Add(entries[i]);
						break;
				}
			}

			var diff = Diff(map, new MapNode(edited));
			return diff.Count == expectedPaths.Count && diff.All(d => expectedPaths.Contains(d.Path));
		});

	// 4: inserting k items into a sequence gives k unexpected items and nothing else.
	[Test]
	public void SequenceInsertsAreUnexpectedOnly() =>
		Gen.Select(TreeGen.Tree.Array[0, 8], Gen.Select(Gen.Int[0, 100], TreeGen.Tree).Array[1, 4]).Sample((items, inserts) =>
		{
			var actual = items.ToList();
			foreach (var (at, item) in inserts)
				actual.Insert(at % (actual.Count + 1), item);
			var diff = Diff(new SeqNode(items), new SeqNode(actual));
			return diff.Count == inserts.Length && diff.All(d => d.Kind == DifferenceKind.Unexpected);
		});

	// 4: in general, k edits give at most 2k differences, each inside an edited place.
	[Test]
	public void EditsStayWithinTheirSubtrees() =>
		Gen.Select(TreeGen.MapWith(3), Gen.Int[0, 3].Array[3, 3], TreeGen.Tree).Sample((map, kinds, replacement) =>
		{
			var entries = map.Entries.ToList();
			var edited = new List<KeyValuePair<ScalarNode, Node>>();
			var roots = new List<string>();
			for (var i = 0; i < entries.Count; i++)
			{
				var (key, value) = entries[i];
				var path = NodePath.Format([PathSegment.ForKey(key.Canonical)]);
				switch (i < 3 ? kinds[i] : 9)
				{
					case 0:
						roots.Add(path);
						break;
					case 1:
						edited.Add(new(key, replacement));
						roots.Add(path);
						break;
					case 2 when value is SeqNode { Items.Count: > 0 } seq:
						edited.Add(new(key, new SeqNode([.. seq.Items.Skip(1)])));
						roots.Add(path);
						break;
					case 3 when value is SeqNode seq:
						edited.Add(new(key, new SeqNode([.. seq.Items, replacement])));
						roots.Add(path);
						break;
					default:
						edited.Add(entries[i]);
						break;
				}
			}

			var diff = Diff(map, new MapNode(edited));
			// A scalar replacement, a dropped first item and an appended item are one difference each; a replaced
			// subtree may hold many, but never outside its own path.
			var bounded = replacement is not ScalarNode || diff.Count <= 2 * roots.Count;
			return bounded && diff.All(d => roots.Exists(r => d.Path == r || d.Path.StartsWith(r + ".", StringComparison.Ordinal) || d.Path.StartsWith(r + "[", StringComparison.Ordinal)));
		});

	// 5
	[Test]
	public void ReorderedKeysAreEqualUnlessOrderMatters() =>
		TreeGen.MapWith(2).Sample(map =>
		{
			var reversed = TreeGen.ReverseKeys(map);
			var ordered = new TreeDiffOptions { MapOrder = MapOrder.Significant };
			return Diff(map, reversed).Count == 0
				&& Diff(map, reversed, ordered).Any(d => d.Kind == DifferenceKind.KeyOrderChanged && d.Path == "$");
		});

	// 6
	[Test]
	public void SwappingTwoDistinctItemsIsADifference() =>
		Gen.Select(TreeGen.Tree.Array[2, 6], Gen.Int[0, 100], Gen.Int[0, 100]).Sample((items, x, y) =>
		{
			int i = x % items.Length, j = y % items.Length;
			if (i == j || TreeDiffer.DeepEquals(items[i], items[j]))
				return true;
			var swapped = items.ToArray();
			(swapped[i], swapped[j]) = (swapped[j], swapped[i]);
			return Diff(new SeqNode(items), new SeqNode(swapped)).Count > 0;
		});

	// 7
	[Test]
	public void MessagesAreDeterministic() =>
		Pair.Sample(p =>
		{
			var first = Render(Diff(p.A, p.B));
			var reparsedA = JsonSnapshot.Parse(TreeGen.ToJson(p.A));
			var reparsedB = JsonSnapshot.Parse(TreeGen.ToJson(p.B));
			return first == Render(Diff(p.A, p.B)) && Strip(first) == Strip(Render(new TreeDiffer().Diff(reparsedA, reparsedB)));
		}, print: p => Show(p) + "\n" + Render(Diff(p.A, p.B)) + "\n---\n" + Render(new TreeDiffer().Diff(JsonSnapshot.Parse(TreeGen.ToJson(p.A)), JsonSnapshot.Parse(TreeGen.ToJson(p.B)))));

	// 8: the alignment matches as many items as a plain dynamic programming LCS.
	[Test]
	public void AlignmentIsALongestCommonSubsequence() =>
		Gen.Select(TreeGen.Scalar.Array[0, 9], TreeGen.Scalar.Array[0, 9]).Sample((e, a) =>
		{
			var lcs = new int[e.Length + 1, a.Length + 1];
			for (var i = e.Length - 1; i >= 0; i--)
				for (var j = a.Length - 1; j >= 0; j--)
					lcs[i, j] = TreeDiffer.DeepEquals(e[i], a[j]) ? lcs[i + 1, j + 1] + 1 : Math.Max(lcs[i + 1, j], lcs[i, j + 1]);
			return TreeDiffer.MatchedItems(new SeqNode(e), new SeqNode(a)) == lcs[0, 0];
		});

	// 9: printing and parsing back gives the same tree, kinds and text included.
	[Test]
	public void PrettyRoundTrips() =>
		TreeGen.Tree.Sample(t =>
		{
			var json = TreeGen.ToJson(t);
			var pretty = JsonSnapshot.Pretty(json);
			return Diff(t, JsonSnapshot.Parse(pretty), new TreeDiffOptions { StrictNumbers = true, MapOrder = MapOrder.Significant }).Count == 0
				&& JsonSnapshot.Pretty(pretty) == pretty;
		}, print: t => TreeGen.ToJson(t) + "\n" + JsonSnapshot.Pretty(TreeGen.ToJson(t)));

	// 13: every reported path resolves in the tree it names, and the rendered lines are plain and capped.
	[Test]
	public void PathsResolveAndMessagesArePlain() =>
		Pair.Sample(p =>
		{
			var diff = Diff(p.A, p.B);
			var resolves = diff.All(d => d.Kind == DifferenceKind.Unexpected
				? NodePath.Resolve(p.B, d.Path) is not null
				: NodePath.Resolve(p.A, d.Path) is not null && (d.ActualPath is null || NodePath.Resolve(p.B, d.ActualPath) is not null));
			var message = SnapshotMessages.Describe(new SnapshotComparison(diff.Count == 0, TreeDifferences.Describe(diff, "verified.json")));
			var lines = message.Split('\n');
			return resolves && !message.Contains('\u001b', StringComparison.Ordinal) && lines.Length <= SnapshotMessageLimits.MaxLines + 2;
		});

	// 14: a ten thousand node diff stays fast, and the positional fallback never throws.
	[Test]
	public void LargeTreesStayFast()
	{
		Node Big(int seed) => new SeqNode([.. Enumerable.Range(0, 2500).Select(i => (Node)new MapNode([
			new(ScalarNode.String("id"), new ScalarNode(ScalarKind.Int, i.ToString(System.Globalization.CultureInfo.InvariantCulture))),
			new(ScalarNode.String("name"), ScalarNode.String(i % 97 == seed ? "changed" : "item " + i)),
			new(ScalarNode.String("tags"), new SeqNode([ScalarNode.String("a"), ScalarNode.String("b")])),
		]))]);
		var expected = Big(-1);
		var actual = Big(3);
		TreeGen.Count(expected).Should().BeGreaterThan(10_000);

		var watch = Stopwatch.StartNew();
		var diff = Diff(expected, actual);
		watch.Stop();
		diff.Should().HaveCount(26).And.OnlyContain(d => d.Kind == DifferenceKind.Changed && d.Path.EndsWith(".name", StringComparison.Ordinal));
		watch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(5));

		var positional = Diff(expected, actual, new TreeDiffOptions { CellCap = 10 });
		positional.Should().HaveCount(26);
	}

	[Test]
	public void PositionalFallbackNeverThrows() =>
		Pair.Sample(p =>
		{
			_ = Diff(p.A, p.B, new TreeDiffOptions { CellCap = 1 });
			return true;
		});

	private static string Show((Node A, Node B) p) => TreeGen.ToJson(p.A) + "  vs  " + TreeGen.ToJson(p.B);

	private static string Render(IReadOnlyList<TreeDifference> diff) =>
		string.Join("\n", TreeDifferences.Describe(diff, "verified.json").Select(l => l[0]));

	// Positions differ between a hand-built tree and a parsed one; the rest of the message must not.
	private static string Strip(string message) =>
		Position().Replace(message, "");

	[System.Text.RegularExpressions.GeneratedRegex(@"   \(verified\.json:\d+:\d+\)")]
	private static partial System.Text.RegularExpressions.Regex Position();

	private static bool HasHugeNumbers(Node node) => node switch
	{
		ScalarNode { IsNumber: true } s => s.Canonical.TrimStart('-').Replace(".", "", StringComparison.Ordinal).Length > 15 || s.Canonical.Contains('e', StringComparison.OrdinalIgnoreCase),
		MapNode m => m.Entries.Any(e => HasHugeNumbers(e.Value)),
		SeqNode q => q.Items.Any(HasHugeNumbers),
		_ => false,
	};
}
