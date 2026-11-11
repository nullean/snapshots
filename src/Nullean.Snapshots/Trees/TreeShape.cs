namespace Nullean.Snapshots.Trees;

/// <summary>
/// Projects a tree onto its shape: what properties it has and what kind each value is, never the values. A string
/// becomes <c>"string"</c>, a boolean <c>false</c>, an integer <c>1</c>, a float <c>1.0</c>; <c>null</c> stays <c>null</c>.
/// Comparing two projected trees reports a property added or dropped, or one that changed kind, and nothing else.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>Map keys are sorted, so the projection does not depend on the order a server wrote them in.</item>
/// <item>
/// A sequence collapses to its distinct shapes: one entry per kind of scalar, one merged map for all its maps, one
/// merged sequence for all its sequences. How many items it had never shows, only which properties any of them had.
/// </item>
/// <item>
/// Maps merge by key union. A key that is <c>null</c> in one item and something else in another takes the something
/// else (a nullable field). Maps whose shared keys disagree about kind (a string in one, an integer in the other) stay
/// separate entries, so no property's kind is lost; which entry such an item joins depends on the order of the items.
/// </item>
/// <item>An empty sequence stays <c>[]</c>, so a list that was emptied is still a difference.</item>
/// </list>
/// </remarks>
public static class TreeShape
{
	/// <summary>The shape of <paramref name="node"/>. Nodes carry no source positions.</summary>
	public static Node Project(Node node)
	{
		ArgumentNullException.ThrowIfNull(node);
		return node switch
		{
			ScalarNode s => Scalar(s),
			MapNode m =>
				new MapNode([
					.. m
						.Entries
						.Select(e => new KeyValuePair<ScalarNode, Node>(ScalarNode.String(e.Key.Canonical), Project(e.Value)))
						.OrderBy(e => e.Key.Canonical, StringComparer.Ordinal)
				]),
			SeqNode s => Collapse([.. s.Items.Select(Project)]),
			_ => throw new ArgumentException($"Unknown node type {node.GetType().Name}.", nameof(node)),
		};
	}

	private static ScalarNode Scalar(ScalarNode node) => node.Kind switch
	{
		ScalarKind.String => ScalarNode.String("string"),
		ScalarKind.Bool => ScalarNode.Bool(false),
		ScalarKind.Int => new ScalarNode(ScalarKind.Int, "1"),
		ScalarKind.Float => new ScalarNode(ScalarKind.Float, "1.0"),
		_ => ScalarNode.Null(),
	};

	// The items are already projected: scalars are one of the five constants, maps are sorted.
	private static SeqNode Collapse(IReadOnlyList<Node> items)
	{
		var scalars = new SortedDictionary<int, ScalarNode>();
		var maps = new List<MapNode>();
		var sequences = new List<Node>();
		foreach (var item in items)
		{
			switch (item)
			{
				case ScalarNode s:
					scalars.TryAdd((int)s.Kind, s);
					break;
				case MapNode m:
					var joined = false;
					for (var i = 0; i < maps.Count && !joined; i++)
					{
						if (TryMerge(maps[i], m) is MapNode merged)
						{
							maps[i] = merged;
							joined = true;
						}
					}

					if (!joined)
						maps.Add(m);
					break;
				case SeqNode s:
					sequences.AddRange(s.Items);
					break;
			}
		}

		var result = new List<Node>(scalars.Values);
		result.AddRange(maps);
		if (items.Any(i => i is SeqNode))
			result.Add(Collapse(sequences));
		return new SeqNode(result);
	}

	/// <summary>The union of two projected nodes, or <see langword="null"/> when a shared key holds different kinds.</summary>
	private static Node? TryMerge(Node a, Node b)
	{
		switch (a, b)
		{
			case (ScalarNode { Kind: ScalarKind.Null }, _):
				return b;
			case (_, ScalarNode { Kind: ScalarKind.Null }):
				return a;
			case (ScalarNode x, ScalarNode y):
				return x.Kind == y.Kind ? x : null;
			case (SeqNode x, SeqNode y):
				return Collapse([.. x.Items, .. y.Items]);
			case (MapNode x, MapNode y):
				var entries = new List<KeyValuePair<ScalarNode, Node>>();
				var right = y.Entries.ToDictionary(e => e.Key.Canonical, e => e.Value, StringComparer.Ordinal);
				foreach (var entry in x.Entries)
				{
					if (right.Remove(entry.Key.Canonical, out var other))
					{
						if (TryMerge(entry.Value, other) is not { } merged)
							return null;
						entries.Add(new(entry.Key, merged));
					}
					else
						entries.Add(entry);
				}

				entries.AddRange(y.Entries.Where(e => right.ContainsKey(e.Key.Canonical)));
				return new MapNode([.. entries.OrderBy(e => e.Key.Canonical, StringComparer.Ordinal)]);
			default:
				return null;
		}
	}
}
