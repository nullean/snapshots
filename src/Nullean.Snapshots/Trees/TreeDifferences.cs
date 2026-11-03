using System.Globalization;

namespace Nullean.Snapshots.Trees;

/// <summary>Renders tree differences as failure message lines.</summary>
public static class TreeDifferences
{
	/// <summary>Values longer than this are cut and end in an ellipsis.</summary>
	public const int MaxValueLength = 60;

	private const int MaxPathColumn = 40;

	/// <summary>
	/// One line per difference: the path, padded so the descriptions line up, then what was expected and found, e.g.
	/// <c>$.items[2].name   expected "kibana"   actual "Kibana"</c>. Missing and changed values point into the verified
	/// file when their position is known: <c>(verified.json:14:5)</c>.
	/// </summary>
	/// <param name="differences">The differences, in order.</param>
	/// <param name="verifiedFileName">How the verified file is named in locations, e.g. <c>verified.json</c>.</param>
	public static IReadOnlyList<IReadOnlyList<string>> Describe(IReadOnlyList<TreeDifference> differences, string verifiedFileName)
	{
		ArgumentNullException.ThrowIfNull(differences);
		var paths = differences.Select(DisplayPath).ToList();
		var width = Math.Min(MaxPathColumn, paths.Count == 0 ? 0 : paths.Take(SnapshotMessageLimits.MaxDifferences).Max(p => p.Length));
		var lines = new List<IReadOnlyList<string>>(differences.Count);
		for (var i = 0; i < differences.Count; i++)
			lines.Add([$"{paths[i].PadRight(width)}   {Describe(differences[i], verifiedFileName)}"]);
		return lines;
	}

	private static string DisplayPath(TreeDifference difference) =>
		difference.ActualPath is null ? difference.Path : $"{difference.Path} (actual {difference.ActualPath})";

	private static string Describe(TreeDifference d, string file) => d.Kind switch
	{
		DifferenceKind.Changed when d is { Expected: ScalarNode { Kind: ScalarKind.String } e, Actual: ScalarNode { Kind: ScalarKind.String } a }
			&& (IsLong(e.Canonical) || IsLong(a.Canonical)) => StringChange(e.Canonical, a.Canonical) + Location(d.Expected, file),
		DifferenceKind.Changed => $"expected {Value(d.Expected)}   actual {Value(d.Actual)}{Location(d.Expected, file)}",
		DifferenceKind.TypeChanged =>
			$"expected ({Kind(d.Expected)}) {Value(d.Expected)}   actual ({Kind(d.Actual)}) {Value(d.Actual)}{Location(d.Expected, file)}",
		DifferenceKind.Missing => $"missing in actual{Location(d.Expected, file)}",
		DifferenceKind.Unexpected => "unexpected in actual",
		DifferenceKind.KeyOrderChanged => $"key order changed: expected {Keys(d.Expected)}   actual {Keys(d.Actual)}{Location(d.Expected, file)}",
		_ => d.Kind.ToString(),
	};

	/// <summary>For long or multi-line strings, the first line that differs.</summary>
	private static string StringChange(string expected, string actual)
	{
		var e = expected.Split('\n');
		var a = actual.Split('\n');
		var line = 0;
		while (line < e.Length && line < a.Length && e[line] == a[line])
			line++;
		var left = line < e.Length ? Quoted(e[line]) : "(no line)";
		var right = line < a.Length ? Quoted(a[line]) : "(no line)";
		return $"line {(line + 1).ToString(CultureInfo.InvariantCulture)}: expected {left}   actual {right}";
	}

	private static bool IsLong(string value) => value.Contains('\n', StringComparison.Ordinal) || value.Length > MaxValueLength;

	private static string Location(Node? node, string file) => node?.Span is { } span ? $"   ({file}:{span})" : string.Empty;

	internal static string Value(Node? node) => node switch
	{
		ScalarNode { Kind: ScalarKind.String } s => Quoted(s.Canonical),
		ScalarNode s => Cut(s.Canonical),
		MapNode m => $"{{…{m.Entries.Count.ToString(CultureInfo.InvariantCulture)} keys}}",
		SeqNode q => $"[…{q.Items.Count.ToString(CultureInfo.InvariantCulture)} items]",
		_ => "missing",
	};

	private static string Kind(Node? node) => node switch
	{
		ScalarNode s => s.Kind.ToString().ToLowerInvariant(),
		MapNode => "map",
		SeqNode => "sequence",
		_ => "missing",
	};

	private static string Keys(Node? node)
	{
		if (node is not MapNode map)
			return "[]";
		var keys = map.Entries.Select(e => e.Key.Canonical).ToList();
		var shown = string.Join(", ", keys.Take(8));
		return keys.Count > 8 ? $"[{shown}, …]" : $"[{shown}]";
	}

	private static string Quoted(string value) => NodePath.Quote(value.Length > MaxValueLength ? value[..(MaxValueLength - 1)] + "…" : value);

	private static string Cut(string value) => value.Length > MaxValueLength ? value[..(MaxValueLength - 1)] + "…" : value;
}

/// <summary>A format whose text parses into a <see cref="Node"/> tree and is compared with the tree differ.</summary>
/// <param name="options">How trees are compared.</param>
public abstract class TreeSnapshotFormat(TreeDiffOptions? options) : SnapshotFormat
{
	/// <summary>How trees are compared.</summary>
	public TreeDiffOptions Options { get; } = options ?? TreeDiffOptions.Default;

	/// <summary>The differ; <see cref="TreeDiffer.Instance"/> unless overridden.</summary>
	protected virtual ITreeDiffer Differ => TreeDiffer.Instance;

	/// <summary>Parses text in this format.</summary>
	/// <exception cref="FormatException">The text is not valid in this format.</exception>
	public abstract Node Parse(string text);

	/// <inheritdoc />
	public override SnapshotComparison Compare(string verified, string actual)
	{
		Node expected;
		try
		{
			expected = Parse(verified);
		}
		catch (FormatException e)
		{
			return new SnapshotComparison(false, [[$"The verified file is not valid {Name}: {e.Message}"]]);
		}

		var differences = Differ.Diff(expected, Parse(actual), Options);
		return differences.Count == 0
			? SnapshotComparison.Match
			: new SnapshotComparison(false, TreeDifferences.Describe(differences, "verified." + Extension));
	}
}
