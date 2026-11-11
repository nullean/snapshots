using System.Globalization;
using System.Numerics;
using System.Text.RegularExpressions;
using Nullean.Snapshots.Trees;
using SharpYaml;
using SharpYaml.Events;

namespace Nullean.Snapshots.Yaml;

/// <summary>Parses and compares YAML.</summary>
public static partial class YamlSnapshot
{
	private const string TagPrefix = "tag:yaml.org,2002:";

	/// <summary>The default format: key order ignored, numbers compared by value.</summary>
	public static TreeSnapshotFormat Format { get; } = new YamlSnapshotFormat(null);

	/// <summary>A format comparing with <paramref name="options"/>.</summary>
	public static TreeSnapshotFormat FormatWith(TreeDiffOptions? options) => options is null ? Format : new YamlSnapshotFormat(options);

	/// <summary>
	/// Parses YAML into a <see cref="Node"/> tree with line and column positions. Plain scalars are typed with the
	/// YAML 1.2 core schema; anchors, aliases and <c>&lt;&lt;</c> merge keys are resolved. A stream of several documents
	/// becomes a sequence of them, which paths show as <c>$doc[n]</c>.
	/// </summary>
	/// <exception cref="FormatException">
	/// The text is not valid YAML, repeats a key in one mapping, uses an unknown alias or a non-scalar key.
	/// </exception>
	public static Node Parse(string yaml) => ParseStream(yaml).Root;

	/// <summary>The tree and how many documents the stream held.</summary>
	internal static (Node Root, int Documents) ParseStream(string yaml)
	{
		ArgumentNullException.ThrowIfNull(yaml);
		try
		{
			var parser = Parser.CreateParser(new StringReader(yaml), 256);
			var documents = new List<Node>();
			var anchors = new Dictionary<string, Node>(StringComparer.Ordinal);
			while (parser.MoveNext())
			{
				if (parser.Current is not DocumentStart)
					continue;
				anchors.Clear();
				if (!parser.MoveNext())
					break;
				documents.Add(parser.Current is DocumentEnd ? ScalarNode.Null() : ReadNode(parser, anchors));
			}

			var root = documents.Count switch
			{
				0 => ScalarNode.Null(),
				1 => documents[0],
				_ => new SeqNode(documents),
			};
			return (root, documents.Count);
		}
		catch (YamlException e)
		{
			throw new FormatException(e.Message, e);
		}
	}

	/// <summary>Compares two YAML texts as trees.</summary>
	public static SnapshotComparison Compare(string expected, string actual, TreeDiffOptions? options = null) => FormatWith(options).Compare(expected, actual);

	private static Node ReadNode(IParser parser, Dictionary<string, Node> anchors)
	{
		var current = parser.Current ?? throw new FormatException("The YAML ended unexpectedly.");
		var span = Span(current.Start);
		Node node;
		switch (current)
		{
			case AnchorAlias alias:
				return anchors.TryGetValue(alias.Value, out var target)
					? target
					: throw new FormatException($"The alias *{alias.Value} at {span} has no anchor.");
			case Scalar scalar:
				node = Resolve(scalar, span);
				break;
			case SequenceStart sequence:
				var items = new List<Node>();
				while (parser.MoveNext() && parser.Current is not SequenceEnd)
					items.Add(ReadNode(parser, anchors));
				node = new SeqNode(items, span);
				Remember(anchors, sequence.Anchor, node);
				return node;
			case MappingStart mapping:
				node = ReadMapping(parser, anchors, span);
				Remember(anchors, mapping.Anchor, node);
				return node;
			default:
				throw new FormatException($"Unexpected {current.GetType().Name} at {span}.");
		}

		Remember(anchors, ((NodeEvent)current).Anchor, node);
		return node;
	}

	/// <summary>
	/// Reads a mapping. Keys given explicitly win over merged ones; of several merged mappings the first wins, as the
	/// YAML merge key type defines. Merged entries take the place of the <c>&lt;&lt;</c> key.
	/// </summary>
	private static MapNode ReadMapping(IParser parser, Dictionary<string, Node> anchors, SourceSpan span)
	{
		var raw = new List<(ScalarNode Key, Node Value, bool Merge)>();
		while (parser.MoveNext() && parser.Current is not MappingEnd)
		{
			var keyEvent = parser.Current!;
			var keyNode = ReadNode(parser, anchors);
			if (keyNode is not ScalarNode key)
				throw new FormatException(
					$"Only scalar keys are supported; the key at {Span(keyEvent.Start)} is a {(keyNode is MapNode ? "mapping" : "sequence")}."
				);
			var merge = keyEvent is Scalar { Value: "<<", Style: ScalarStyle.Plain or ScalarStyle.Any } s &&
				(s.Tag is null || s.Tag == "!!merge" || s.Tag == TagPrefix + "merge");
			parser.MoveNext();
			raw.Add((key, ReadNode(parser, anchors), merge));
		}

		var explicitKeys = new HashSet<string>(StringComparer.Ordinal);
		foreach (var (key, _, merge) in raw)
		{
			if (!merge && !explicitKeys.Add(KeyId(key)))
				throw new FormatException($"The key {NodePath.Quote(key.Canonical)} appears twice in one mapping, at {key.Span}.");
		}

		var entries = new List<KeyValuePair<ScalarNode, Node>>();
		var added = new HashSet<string>(StringComparer.Ordinal);
		foreach (var (key, value, merge) in raw)
		{
			if (!merge)
			{
				entries.Add(new(key, value));
				added.Add(KeyId(key));
				continue;
			}

			IEnumerable<Node> sources = value is SeqNode list ? list.Items : [value];
			foreach (var source in sources)
			{
				if (source is not MapNode map)
					throw new FormatException($"A merge key at {key.Span} needs a mapping or a sequence of mappings.");
				foreach (var entry in map.Entries)
				{
					var id = KeyId(entry.Key);
					if (!explicitKeys.Contains(id) && added.Add(id))
						entries.Add(entry);
				}
			}
		}

		return new MapNode(entries, span);
	}

	/// <summary>Types a scalar: explicit tags first, quoted and block scalars are strings, plain ones go by the core schema.</summary>
	internal static ScalarNode Resolve(Scalar scalar, SourceSpan? span)
	{
		var value = scalar.Value;
		var tag = scalar.Tag switch
		{
			null or "" or "!" => null,
			var t when t.StartsWith("!!", StringComparison.Ordinal) => t[2..],
			var t when t.StartsWith(TagPrefix, StringComparison.Ordinal) => t[TagPrefix.Length..],
			_ => null, // a local tag: typed as if untagged
		};

		if (tag is not null)
		{
			return tag switch
			{
				"str" => ScalarNode.String(value, span),
				"null" => ScalarNode.Null(span),
				"bool" => ScalarNode.Bool(value.Equals("true", StringComparison.OrdinalIgnoreCase), span),
				"int" => Int(value, span) ?? new ScalarNode(ScalarKind.Int, value, span),
				"float" => new ScalarNode(ScalarKind.Float, value, span),
				_ => Plain(value, span),
			};
		}

		return scalar.Style is ScalarStyle.Plain or ScalarStyle.Any && scalar.IsPlainImplicit is not false
			? Plain(value, span)
			: ScalarNode.String(value, span);
	}

	/// <summary>The YAML 1.2.2 core schema (§10.3.2): null, bool, int (decimal, 0o, 0x), float, or else a string.</summary>
	internal static ScalarNode Plain(string value, SourceSpan? span)
	{
		if (value is "" or "~" or "null" or "Null" or "NULL")
			return ScalarNode.Null(span);
		if (value is "true" or "True" or "TRUE")
			return ScalarNode.Bool(true, span);
		if (value is "false" or "False" or "FALSE")
			return ScalarNode.Bool(false, span);
		if (Int(value, span) is { } integer)
			return integer;
		if (FloatPattern().IsMatch(value))
			return new ScalarNode(ScalarKind.Float, value, span);
		return ScalarNode.String(value, span);
	}

	/// <summary>Decimal integers as written; octal and hexadecimal ones as their decimal value, so <c>0x1F</c> equals <c>31</c>.</summary>
	private static ScalarNode? Int(string value, SourceSpan? span)
	{
		if (DecimalPattern().IsMatch(value))
			return new ScalarNode(ScalarKind.Int, value, span);
		if (value.Length > 2 && value[0] == '0' && (value[1] == 'o' || value[1] == 'x'))
		{
			var digits = value.AsSpan(2);
			var radix = value[1] == 'o' ? 8 : 16;
			var number = BigInteger.Zero;
			foreach (var c in digits)
			{
				var d = char.IsAsciiDigit(c) ? c - '0' : char.IsAsciiHexDigit(c) ? char.ToLowerInvariant(c) - 'a' + 10 : 99;
				if (d >= radix)
					return null;
				number = (number * radix) + d;
			}

			return new ScalarNode(ScalarKind.Int, number.ToString(CultureInfo.InvariantCulture), span);
		}

		return null;
	}

	private static void Remember(Dictionary<string, Node> anchors, string? anchor, Node node)
	{
		if (!string.IsNullOrEmpty(anchor))
			anchors[anchor] = node;
	}

	private static string KeyId(ScalarNode key) => (char)('a' + (int)key.Kind) + key.Canonical;

	private static SourceSpan Span(Mark mark) => new(mark.Line + 1, mark.Column + 1);

	[GeneratedRegex("^[-+]?[0-9]+$")]
	private static partial Regex DecimalPattern();

	[GeneratedRegex(@"^([-+]?(\.[0-9]+|[0-9]+(\.[0-9]*)?)([eE][-+]?[0-9]+)?|[-+]?\.(inf|Inf|INF)|\.(nan|NaN|NAN))$")]
	private static partial Regex FloatPattern();
}

/// <summary>YAML stored as written and compared as a tree.</summary>
internal sealed class YamlSnapshotFormat(TreeDiffOptions? options) : TreeSnapshotFormat(options)
{
	public override string Name => "YAML";

	public override string Extension => "yml";

	public override Node Parse(string text) => YamlSnapshot.Parse(text);

	/// <summary>
	/// Like the base comparison, except that the documents of a multi-document stream pair up by position (they are
	/// separate files in one, not items to align), and their paths read <c>$doc[n]</c>.
	/// </summary>
	public override SnapshotComparison Compare(string verified, string actual)
	{
		(Node Root, int Documents) expected;
		try
		{
			expected = YamlSnapshot.ParseStream(verified);
		}
		catch (FormatException e)
		{
			return new SnapshotComparison(false, [[$"The verified file is not valid {Name}: {e.Message}"]]);
		}

		var found = YamlSnapshot.ParseStream(actual);
		IReadOnlyList<TreeDifference> differences;
		if (expected.Documents < 2 && found.Documents < 2)
			differences = Differ.Diff(expected.Root, found.Root, Options);
		else
		{
			var e = Documents(expected);
			var a = Documents(found);
			var all = new List<TreeDifference>();
			for (var i = 0; i < Math.Max(e.Count, a.Count); i++)
			{
				var prefix = $"$doc[{i.ToString(System.Globalization.CultureInfo.InvariantCulture)}]";
				if (i >= a.Count)
					all.Add(new TreeDifference(DifferenceKind.Missing, prefix, e[i], null));
				else if (i >= e.Count)
					all.Add(new TreeDifference(DifferenceKind.Unexpected, prefix, null, a[i]));
				else
				{
					all.AddRange(
						Differ.Diff(e[i], a[i], Options).Select(
							d => d with { Path = prefix + d.Path[1..], ActualPath = d.ActualPath is null ? null : prefix + d.ActualPath[1..], }
						)
					);
				}
			}

			differences = all;
		}

		return differences.Count == 0
			? SnapshotComparison.Match
			: new SnapshotComparison(false, TreeDifferences.Describe(differences, "verified." + Extension));
	}

	private static IReadOnlyList<Node> Documents((Node Root, int Documents) stream) => stream.Documents >= 2 ? ((SeqNode)stream.Root).Items : [stream.Root];
}
