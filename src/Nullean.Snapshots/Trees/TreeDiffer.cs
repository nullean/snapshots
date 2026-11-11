namespace Nullean.Snapshots.Trees;

/// <summary>Whether the order of map keys matters.</summary>
public enum MapOrder
{
	/// <summary>Maps are equal when they hold the same entries in any order. The default.</summary>
	Insensitive,

	/// <summary>A different order of the same keys is reported as <see cref="DifferenceKind.KeyOrderChanged"/>.</summary>
	Significant,
}

/// <summary>How two trees are compared.</summary>
public sealed record TreeDiffOptions
{
	/// <summary>The defaults: key order ignored, numbers compared by value, nothing ignored.</summary>
	public static TreeDiffOptions Default { get; } = new();

	/// <summary>Whether key order matters.</summary>
	public MapOrder MapOrder { get; init; }

	/// <summary>Compare numbers by their kind and text, so <c>1</c> and <c>1.0</c> differ.</summary>
	public bool StrictNumbers { get; init; }

	/// <summary>Treat a key whose value is <c>null</c> on one side the same as the key missing on the other.</summary>
	public bool NullEqualsMissing { get; init; }

	/// <summary>Paths left out of the comparison: <c>$.meta.generatedAt</c>, <c>$..id</c>, <c>$.items[*].etag</c>.</summary>
	public IReadOnlyList<string> IgnorePaths { get; init; } = [];

	/// <summary>
	/// Sequences whose lengths multiply to more than this are aligned by position instead of by LCS, which is quadratic
	/// in time and memory.
	/// </summary>
	public long CellCap { get; init; } = 4_000_000;
}

/// <summary>The kinds of differences.</summary>
public enum DifferenceKind
{
	/// <summary>A scalar has a different value.</summary>
	Changed,

	/// <summary>A value has a different kind: <c>1</c> against <c>"1"</c>, or a map against a sequence.</summary>
	TypeChanged,

	/// <summary>The verified value is not in the actual tree.</summary>
	Missing,

	/// <summary>The actual tree has a value the verified one does not.</summary>
	Unexpected,

	/// <summary>A map has the same keys in a different order (<see cref="MapOrder.Significant"/> only).</summary>
	KeyOrderChanged,
}

/// <summary>One difference between a verified and an actual tree.</summary>
/// <param name="Kind">What changed.</param>
/// <param name="Path">Where, in the verified tree (in the actual tree for <see cref="DifferenceKind.Unexpected"/>).</param>
/// <param name="Expected">The verified node, if there is one.</param>
/// <param name="Actual">The actual node, if there is one.</param>
/// <param name="ActualPath">The path in the actual tree, when it differs from <paramref name="Path"/>.</param>
public sealed record TreeDifference(DifferenceKind Kind, string Path, Node? Expected, Node? Actual, string? ActualPath = null);

/// <summary>Compares two trees.</summary>
public interface ITreeDiffer
{
	/// <summary>Every difference between <paramref name="expected"/> and <paramref name="actual"/>, in a stable order.</summary>
	IReadOnlyList<TreeDifference> Diff(Node expected, Node actual, TreeDiffOptions? options = null);
}

/// <summary>
/// The tree differ. Maps compare by key union. Sequences keep their order: they are aligned by the longest common
/// subsequence of equal items (positionally above <see cref="TreeDiffOptions.CellCap"/>), and an unmatched verified
/// item is paired with the unmatched actual item at the same place in its gap when the two are similar, so a changed
/// field reads as <c>$.items[2].name</c> rather than a whole item replaced. Moves are not detected: they read as
/// missing plus unexpected.
/// </summary>
public sealed class TreeDiffer : ITreeDiffer
{
	/// <summary>The shared instance.</summary>
	public static TreeDiffer Instance { get; } = new();

	/// <inheritdoc />
	public IReadOnlyList<TreeDifference> Diff(Node expected, Node actual, TreeDiffOptions? options = null)
	{
		ArgumentNullException.ThrowIfNull(expected);
		ArgumentNullException.ThrowIfNull(actual);
		var run = new Run(options ?? TreeDiffOptions.Default);
		run.Diff(expected, actual);
		return run.Differences;
	}

	/// <summary>True when <see cref="Diff"/> would find no difference.</summary>
	public static bool DeepEquals(Node expected, Node actual, TreeDiffOptions? options = null)
	{
		ArgumentNullException.ThrowIfNull(expected);
		ArgumentNullException.ThrowIfNull(actual);
		return new Run(options ?? TreeDiffOptions.Default).Equal(expected, actual);
	}

	/// <summary>
	/// How many verified items the sequence alignment matched. Exposed for the test that checks it against a naive
	/// longest common subsequence.
	/// </summary>
	internal static int MatchedItems(SeqNode expected, SeqNode actual, TreeDiffOptions? options = null) =>
		new Run(options ?? TreeDiffOptions.Default).Align(expected.Items, actual.Items).Count(o => o.Kind == OpKind.Match);

	internal enum OpKind
	{
		Match,
		Delete,
		Insert
	}

	internal readonly record struct Op(OpKind Kind, int Expected, int Actual);

	private sealed class Run(TreeDiffOptions options)
	{
		private readonly IReadOnlyList<IReadOnlyList<PathSegment>> _ignore = [.. options.IgnorePaths.Select(NodePath.Parse)];
		private readonly List<PathSegment> _path = [];
		private readonly List<PathSegment> _actualPath = [];
		private readonly bool _strict = options.StrictNumbers;
		private readonly bool _ordered = options.MapOrder == MapOrder.Significant;

		// Hashes rule equality out, never in. Ignored paths and null-as-missing make equality depend on more than a
		// node's own content, so they fall back to a hash of the node's kind alone.
		private readonly bool _shallowHash = options.IgnorePaths.Count > 0 || options.NullEqualsMissing;

		public List<TreeDifference> Differences { get; } = [];

		public void Diff(Node expected, Node actual)
		{
			if (Ignored())
				return;
			switch (expected, actual)
			{
				case (ScalarNode e, ScalarNode a):
					if (!ScalarEqual(e, a))
						Add(Comparable(e, a) ? DifferenceKind.Changed : DifferenceKind.TypeChanged, e, a);
					break;
				case (MapNode e, MapNode a):
					DiffMap(e, a);
					break;
				case (SeqNode e, SeqNode a):
					DiffSeq(e, a);
					break;
				default:
					Add(DifferenceKind.TypeChanged, expected, actual);
					break;
			}
		}

		private void DiffMap(MapNode expected, MapNode actual)
		{
			var actualByKey = new Dictionary<string, Node>(StringComparer.Ordinal);
			foreach (var (key, value) in actual.Entries)
				actualByKey.TryAdd(KeyId(key), value);
			var expectedKeys = new HashSet<string>(expected.Entries.Select(e => KeyId(e.Key)), StringComparer.Ordinal);

			if (_ordered)
			{
				var expectedOrder = expected.Entries.Select(e => KeyId(e.Key)).Where(actualByKey.ContainsKey).ToList();
				var actualOrder = actual.Entries.Select(e => KeyId(e.Key)).Where(expectedKeys.Contains).ToList();
				if (!expectedOrder.SequenceEqual(actualOrder, StringComparer.Ordinal))
					Add(DifferenceKind.KeyOrderChanged, expected, actual);
			}

			foreach (var (key, value) in expected.Entries)
			{
				Push(PathSegment.ForKey(NodePath.KeyText(key)));
				if (actualByKey.TryGetValue(KeyId(key), out var other))
					Diff(value, other);
				else if (!IsNullSkippable(value) && !Ignored())
					Add(DifferenceKind.Missing, value, null);
				Pop();
			}

			foreach (var (key, value) in actual.Entries)
			{
				if (expectedKeys.Contains(KeyId(key)))
					continue;
				Push(PathSegment.ForKey(NodePath.KeyText(key)));
				if (!IsNullSkippable(value) && !Ignored())
					Add(DifferenceKind.Unexpected, null, value);
				Pop();
			}
		}

		private void DiffSeq(SeqNode expected, SeqNode actual)
		{
			var e = expected.Items;
			var a = actual.Items;
			var ops = Align(e, a);
			var deletes = new List<int>();
			var inserts = new List<int>();
			foreach (var op in ops)
			{
				switch (op.Kind)
				{
					case OpKind.Delete:
						deletes.Add(op.Expected);
						break;
					case OpKind.Insert:
						inserts.Add(op.Actual);
						break;
					default:
						Gap(e, a, deletes, inserts);
						break;
				}
			}

			Gap(e, a, deletes, inserts);
		}

		/// <summary>
		/// Reports one gap between matched items: the i-th verified item against the i-th actual item when they are
		/// similar but not equal, the rest as missing and unexpected.
		/// </summary>
		private void Gap(IReadOnlyList<Node> e, IReadOnlyList<Node> a, List<int> deletes, List<int> inserts)
		{
			var pairs = Math.Min(deletes.Count, inserts.Count);
			for (var k = 0; k < pairs; k++)
			{
				int i = deletes[k], j = inserts[k];
				if (Similar(e[i], a[j], i) && !EqualAt(e[i], a[j], i))
				{
					Push(PathSegment.ForIndex(i), PathSegment.ForIndex(j));
					Diff(e[i], a[j]);
					Pop();
				}
				else
				{
					AddAt(DifferenceKind.Missing, i, i, e[i], null);
					AddAt(DifferenceKind.Unexpected, j, j, null, a[j]);
				}
			}

			for (var k = pairs; k < deletes.Count; k++)
				AddAt(DifferenceKind.Missing, deletes[k], deletes[k], e[deletes[k]], null);
			for (var k = pairs; k < inserts.Count; k++)
				AddAt(DifferenceKind.Unexpected, inserts[k], inserts[k], null, a[inserts[k]]);
			deletes.Clear();
			inserts.Clear();
		}

		/// <summary>
		/// Aligns two sequences: the longest common subsequence of equal items, read from the front with matches first,
		/// or by position when the table would exceed the cell cap.
		/// </summary>
		public List<Op> Align(IReadOnlyList<Node> e, IReadOnlyList<Node> a)
		{
			int n = e.Count, m = a.Count;
			var ops = new List<Op>(n + m);
			if ((long)n * m > options.CellCap)
			{
				for (var k = 0; k < Math.Max(n, m); k++)
				{
					if (k < n && k < m && EqualAt(e[k], a[k], k))
						ops.Add(new Op(OpKind.Match, k, k));
					else
					{
						if (k < n)
							ops.Add(new Op(OpKind.Delete, k, -1));
						if (k < m)
							ops.Add(new Op(OpKind.Insert, -1, k));
					}
				}

				return ops;
			}

			var eh = e.Select(Hash).ToArray();
			var ah = a.Select(Hash).ToArray();
			var width = m + 1;
			var table = new int[(n + 1) * width];

			bool Eq(int i, int j) => eh[i] == ah[j] && EqualAt(e[i], a[j], i);

			for (var i = n - 1; i >= 0; i--)
			{
				for (var j = m - 1; j >= 0; j--)
				{
					table[(i * width) + j] = Eq(i, j)
						? table[((i + 1) * width) + j + 1] + 1
						: Math.Max(table[((i + 1) * width) + j], table[(i * width) + j + 1]);
				}
			}

			int x = 0, y = 0;
			while (x < n && y < m)
			{
				if (table[(x * width) + y] == table[((x + 1) * width) + y + 1] + 1 && Eq(x, y))
				{
					ops.Add(new Op(OpKind.Match, x++, y++));
				}
				else if (table[((x + 1) * width) + y] >= table[(x * width) + y + 1])
					ops.Add(new Op(OpKind.Delete, x++, -1));
				else
					ops.Add(new Op(OpKind.Insert, -1, y++));
			}

			while (x < n)
				ops.Add(new Op(OpKind.Delete, x++, -1));
			while (y < m)
				ops.Add(new Op(OpKind.Insert, -1, y++));
			return ops;
		}

		/// <summary>Equality under the options, consistent with <see cref="Diff"/> finding nothing.</summary>
		public bool Equal(Node expected, Node actual)
		{
			if (Ignored())
				return true;
			switch (expected, actual)
			{
				case (ScalarNode e, ScalarNode a):
					return ScalarEqual(e, a);
				case (SeqNode e, SeqNode a):
					var shared = Math.Min(e.Items.Count, a.Items.Count);
					for (var i = 0; i < shared; i++)
					{
						if (!EqualAt(e.Items[i], a.Items[i], i))
							return false;
					}

					// Extra items only count when their path is not ignored, as Diff reports them.
					for (var i = shared; i < Math.Max(e.Items.Count, a.Items.Count); i++)
					{
						Push(PathSegment.ForIndex(i));
						var ignored = Ignored();
						Pop();
						if (!ignored)
							return false;
					}

					return true;
				case (MapNode e, MapNode a):
					return MapEqual(e, a);
				default:
					return false;
			}
		}

		private bool MapEqual(MapNode expected, MapNode actual)
		{
			var actualByKey = new Dictionary<string, Node>(StringComparer.Ordinal);
			foreach (var (key, value) in actual.Entries)
				actualByKey.TryAdd(KeyId(key), value);
			var seen = 0;
			foreach (var (key, value) in expected.Entries)
			{
				Push(PathSegment.ForKey(NodePath.KeyText(key)));
				try
				{
					if (actualByKey.TryGetValue(KeyId(key), out var other))
					{
						seen++;
						if (!Equal(value, other))
							return false;
					}
					else if (!IsNullSkippable(value) && !Ignored())
						return false;
				}
				finally
				{
					Pop();
				}
			}

			if (seen != actualByKey.Count)
			{
				var expectedKeys = new HashSet<string>(expected.Entries.Select(e => KeyId(e.Key)), StringComparer.Ordinal);
				foreach (var (key, value) in actual.Entries)
				{
					if (expectedKeys.Contains(KeyId(key)))
						continue;
					Push(PathSegment.ForKey(NodePath.KeyText(key)));
					var skippable = IsNullSkippable(value) || Ignored();
					Pop();
					if (!skippable)
						return false;
				}
			}

			if (_ordered)
			{
				var expectedKeys = new HashSet<string>(expected.Entries.Select(e => KeyId(e.Key)), StringComparer.Ordinal);
				var expectedOrder = expected.Entries.Select(e => KeyId(e.Key)).Where(actualByKey.ContainsKey);
				var actualOrder = actual.Entries.Select(e => KeyId(e.Key)).Where(expectedKeys.Contains);
				if (!expectedOrder.SequenceEqual(actualOrder, StringComparer.Ordinal))
					return false;
			}

			return true;
		}

		/// <summary>
		/// Whether an unmatched verified item and actual item are close enough to diff field by field: the same node
		/// type, and for maps a shared equal entry, for sequences a shared equal item. Scalars always pair.
		/// </summary>
		private bool Similar(Node expected, Node actual, int index)
		{
			Push(PathSegment.ForIndex(index));
			try
			{
				switch (expected, actual)
				{
					case (ScalarNode, ScalarNode):
						return true;
					case (MapNode e, MapNode a):
						if (e.Entries.Count == 0 || a.Entries.Count == 0)
							return true;
						var byKey = new Dictionary<string, Node>(StringComparer.Ordinal);
						foreach (var (key, value) in a.Entries)
							byKey.TryAdd(KeyId(key), value);
						foreach (var (key, value) in e.Entries)
						{
							if (!byKey.TryGetValue(KeyId(key), out var other))
								continue;
							Push(PathSegment.ForKey(NodePath.KeyText(key)));
							var equal = Equal(value, other);
							Pop();
							if (equal)
								return true;
						}

						return false;
					case (SeqNode e, SeqNode a):
						if (e.Items.Count == 0 || a.Items.Count == 0)
							return true;
						var hashes = a.Items.Select(Hash).ToArray();
						for (var i = 0; i < e.Items.Count; i++)
						{
							var h = Hash(e.Items[i]);
							for (var j = 0; j < a.Items.Count; j++)
							{
								if (hashes[j] == h && EqualAt(e.Items[i], a.Items[j], i))
									return true;
							}
						}

						return false;
					default:
						return false;
				}
			}
			finally
			{
				Pop();
			}
		}

		private bool EqualAt(Node expected, Node actual, int index)
		{
			Push(PathSegment.ForIndex(index));
			try
			{
				return Equal(expected, actual);
			}
			finally
			{
				Pop();
			}
		}

		private bool ScalarEqual(ScalarNode e, ScalarNode a)
		{
			if (e.Kind == a.Kind && string.Equals(e.Canonical, a.Canonical, StringComparison.Ordinal))
				return true;
			if (_strict || !e.IsNumber || !a.IsNumber)
				return false;
			return NumberValue.TryParse(e.Canonical, out var x) && NumberValue.TryParse(a.Canonical, out var y) && x == y;
		}

		private bool Comparable(ScalarNode e, ScalarNode a) => e.Kind == a.Kind || (!_strict && e.IsNumber && a.IsNumber);

		private bool IsNullSkippable(Node value) => options.NullEqualsMissing && value is ScalarNode { Kind: ScalarKind.Null };

		private string KeyId(ScalarNode key)
		{
			if (key.IsNumber && !_strict && NumberValue.TryParse(key.Canonical, out var number))
				return "n" + number.Negative + number.Digits + "e" + number.Exponent;
			return (char)('a' + (int)key.Kind) + key.Canonical;
		}

		private int Hash(Node node)
		{
			if (_shallowHash)
				return node.GetType().GetHashCode();
			switch (node)
			{
				case ScalarNode s when s.IsNumber && !_strict && NumberValue.TryParse(s.Canonical, out var number):
					return HashCode.Combine(1, number);
				case ScalarNode s:
					return HashCode.Combine(2, s.Kind, StringComparer.Ordinal.GetHashCode(s.Canonical));
				case SeqNode q:
					var seq = new HashCode();
					seq.Add(3);
					foreach (var item in q.Items)
						seq.Add(Hash(item));
					return seq.ToHashCode();
				case MapNode m:
					var unordered = 0;
					var ordered = new HashCode();
					foreach (var (key, value) in m.Entries)
					{
						var entry = HashCode.Combine(StringComparer.Ordinal.GetHashCode(KeyId(key)), Hash(value));
						unordered += entry;
						ordered.Add(entry);
					}

					return HashCode.Combine(4, m.Entries.Count, _ordered ? ordered.ToHashCode() : unordered);
				default:
					return 0;
			}
		}

		private bool Ignored() => _ignore.Count > 0 && _ignore.Any(p => NodePath.Matches(p, _path));

		private void Add(DifferenceKind kind, Node? expected, Node? actual)
		{
			var path = NodePath.Format(kind == DifferenceKind.Unexpected ? _actualPath : _path);
			var actualPath = NodePath.Format(_actualPath);
			Differences.Add(new TreeDifference(kind, path, expected, actual, kind == DifferenceKind.Unexpected || actualPath == path ? null : actualPath));
		}

		private void AddAt(DifferenceKind kind, int expectedIndex, int actualIndex, Node? expected, Node? actual)
		{
			Push(PathSegment.ForIndex(expectedIndex), PathSegment.ForIndex(actualIndex));
			if (!Ignored())
				Add(kind, expected, actual);
			Pop();
		}

		private void Push(PathSegment segment) => Push(segment, segment);

		private void Push(PathSegment expected, PathSegment actual)
		{
			_path.Add(expected);
			_actualPath.Add(actual);
		}

		private void Pop()
		{
			_path.RemoveAt(_path.Count - 1);
			_actualPath.RemoveAt(_actualPath.Count - 1);
		}
	}
}
