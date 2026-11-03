using System.Globalization;
using System.Text;

namespace Nullean.Snapshots.Trees;

/// <summary>One step in a path: a map key or a sequence index.</summary>
public readonly record struct PathSegment
{
	private PathSegment(string? key, int index, PathSegmentKind kind)
	{
		Key = key;
		Index = index;
		Kind = kind;
	}

	/// <summary>What kind of step this is.</summary>
	public PathSegmentKind Kind { get; }

	/// <summary>The key, for <see cref="PathSegmentKind.Key"/> and <see cref="PathSegmentKind.Descendant"/>.</summary>
	public string? Key { get; }

	/// <summary>The index, for <see cref="PathSegmentKind.Index"/>.</summary>
	public int Index { get; }

	/// <summary>A map key.</summary>
	public static PathSegment ForKey(string key) => new(key, -1, PathSegmentKind.Key);

	/// <summary>A sequence index.</summary>
	public static PathSegment ForIndex(int index) => new(null, index, PathSegmentKind.Index);

	internal static PathSegment AnyKey { get; } = new(null, -1, PathSegmentKind.AnyKey);
	internal static PathSegment AnyIndex { get; } = new(null, -1, PathSegmentKind.AnyIndex);
	internal static PathSegment Descendant(string key) => new(key, -1, PathSegmentKind.Descendant);
}

/// <summary>The kinds of path steps; the wildcard kinds only appear in ignore patterns.</summary>
public enum PathSegmentKind
{
	/// <summary><c>.key</c> or <c>["key"]</c>.</summary>
	Key,

	/// <summary><c>[n]</c>.</summary>
	Index,

	/// <summary><c>.*</c>: any key.</summary>
	AnyKey,

	/// <summary><c>[*]</c>: any index.</summary>
	AnyIndex,

	/// <summary><c>..key</c>: the key at any depth.</summary>
	Descendant,
}

/// <summary>
/// JSONPath-like paths: <c>$</c>, <c>.key</c>, <c>["key with.special"]</c>, <c>[n]</c>. Ignore patterns may also use
/// <c>.*</c>, <c>[*]</c> and <c>..key</c>.
/// </summary>
public static class NodePath
{
	/// <summary>Formats <paramref name="segments"/> as a path starting at <c>$</c>.</summary>
	public static string Format(IEnumerable<PathSegment> segments)
	{
		var path = new StringBuilder("$");
		foreach (var segment in segments)
			Append(path, segment);
		return path.ToString();
	}

	internal static void Append(StringBuilder path, PathSegment segment)
	{
		switch (segment.Kind)
		{
			case PathSegmentKind.Index:
				path.Append('[').Append(segment.Index.ToString(CultureInfo.InvariantCulture)).Append(']');
				break;
			case PathSegmentKind.Key when IsIdentifier(segment.Key!):
				path.Append('.').Append(segment.Key);
				break;
			case PathSegmentKind.Key:
				path.Append('[').Append(Quote(segment.Key!)).Append(']');
				break;
			case PathSegmentKind.AnyKey:
				path.Append(".*");
				break;
			case PathSegmentKind.AnyIndex:
				path.Append("[*]");
				break;
			case PathSegmentKind.Descendant:
				path.Append("..").Append(segment.Key);
				break;
		}
	}

	/// <summary>Parses a path or an ignore pattern.</summary>
	/// <exception cref="FormatException">It is not one.</exception>
	public static IReadOnlyList<PathSegment> Parse(string path)
	{
		ArgumentNullException.ThrowIfNull(path);
		if (!path.StartsWith('$'))
			throw new FormatException($"A path starts with '$': {path}");
		var segments = new List<PathSegment>();
		var i = 1;
		while (i < path.Length)
		{
			if (path[i] == '.')
			{
				var descendant = i + 1 < path.Length && path[i + 1] == '.';
				i += descendant ? 2 : 1;
				if (!descendant && i < path.Length && path[i] == '*')
				{
					segments.Add(PathSegment.AnyKey);
					i++;
					continue;
				}

				var start = i;
				while (i < path.Length && path[i] is not ('.' or '['))
					i++;
				if (i == start)
					throw new FormatException($"Empty key in path: {path}");
				var key = path[start..i];
				segments.Add(descendant ? PathSegment.Descendant(key) : PathSegment.ForKey(key));
			}
			else if (path[i] == '[')
			{
				var close = path[i + 1] == '"' ? QuotedEnd(path, i + 1) + 1 : path.IndexOf(']', i);
				if (close < 0 || close >= path.Length || path[close] != ']')
					throw new FormatException($"Unclosed '[' in path: {path}");
				var inner = path[(i + 1)..close];
				if (inner == "*")
					segments.Add(PathSegment.AnyIndex);
				else if (inner.StartsWith('"'))
					segments.Add(PathSegment.ForKey(Unquote(inner, path)));
				else if (int.TryParse(inner, NumberStyles.None, CultureInfo.InvariantCulture, out var index))
					segments.Add(PathSegment.ForIndex(index));
				else
					throw new FormatException($"Expected an index, * or a quoted key in path: {path}");
				i = close + 1;
			}
			else
				throw new FormatException($"Unexpected '{path[i]}' in path: {path}");
		}

		return segments;
	}

	/// <summary>The node at <paramref name="path"/> in <paramref name="root"/>, or null when there is none.</summary>
	public static Node? Resolve(Node root, string path)
	{
		var node = root;
		foreach (var segment in Parse(path))
		{
			node = (node, segment.Kind) switch
			{
				(SeqNode seq, PathSegmentKind.Index) => segment.Index < seq.Items.Count ? seq.Items[segment.Index] : null,
				(MapNode map, PathSegmentKind.Key) => map.Entries.FirstOrDefault(e => KeyText(e.Key) == segment.Key).Value,
				_ => null,
			};
			if (node is null)
				return null;
		}

		return node;
	}

	/// <summary>How a map key reads in a path: the string, or the scalar's canonical text.</summary>
	public static string KeyText(ScalarNode key) => key.Canonical;

	/// <summary>True when <paramref name="pattern"/> (with wildcards) matches the whole of <paramref name="path"/>.</summary>
	public static bool Matches(IReadOnlyList<PathSegment> pattern, IReadOnlyList<PathSegment> path) =>
		Matches(pattern, 0, path, 0);

	private static bool Matches(IReadOnlyList<PathSegment> pattern, int p, IReadOnlyList<PathSegment> path, int s)
	{
		if (p == pattern.Count)
			return s == path.Count;
		var step = pattern[p];
		if (step.Kind == PathSegmentKind.Descendant)
		{
			for (var at = s; at < path.Count; at++)
			{
				if (path[at].Kind == PathSegmentKind.Key && path[at].Key == step.Key && Matches(pattern, p + 1, path, at + 1))
					return true;
			}

			return false;
		}

		if (s == path.Count)
			return false;
		var segment = path[s];
		var ok = step.Kind switch
		{
			PathSegmentKind.Key => segment.Kind == PathSegmentKind.Key && segment.Key == step.Key,
			PathSegmentKind.Index => segment.Kind == PathSegmentKind.Index && segment.Index == step.Index,
			PathSegmentKind.AnyKey => segment.Kind == PathSegmentKind.Key,
			PathSegmentKind.AnyIndex => segment.Kind == PathSegmentKind.Index,
			_ => false,
		};
		return ok && Matches(pattern, p + 1, path, s + 1);
	}

	private static int QuotedEnd(string path, int open)
	{
		for (var i = open + 1; i < path.Length; i++)
		{
			if (path[i] == '\\')
				i++;
			else if (path[i] == '"')
				return i;
		}

		return -1;
	}

	private static bool IsIdentifier(string key) =>
		key.Length > 0 && (char.IsAsciiLetter(key[0]) || key[0] == '_') && key.All(c => char.IsAsciiLetterOrDigit(c) || c == '_');

	/// <summary>
	/// <paramref name="value"/> as a JSON string literal: quotes, backslashes and control characters escaped, everything
	/// else (non-ASCII included) left readable.
	/// </summary>
	public static string Quote(string value)
	{
		var quoted = new StringBuilder(value.Length + 2).Append('"');
		foreach (var c in value)
		{
			switch (c)
			{
				case '"':
					quoted.Append("\\\"");
					break;
				case '\\':
					quoted.Append("\\\\");
					break;
				case '\n':
					quoted.Append("\\n");
					break;
				case '\r':
					quoted.Append("\\r");
					break;
				case '\t':
					quoted.Append("\\t");
					break;
				case < ' ':
					quoted.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
					break;
				default:
					quoted.Append(c);
					break;
			}
		}

		return quoted.Append('"').ToString();
	}

	private static string Unquote(string literal, string path)
	{
		var value = new StringBuilder(literal.Length);
		for (var i = 1; i < literal.Length - 1; i++)
		{
			var c = literal[i];
			if (c != '\\')
			{
				value.Append(c);
				continue;
			}

			if (++i >= literal.Length - 1)
				throw new FormatException($"Bad escape in path: {path}");
			switch (literal[i])
			{
				case 'n':
					value.Append('\n');
					break;
				case 'r':
					value.Append('\r');
					break;
				case 't':
					value.Append('\t');
					break;
				case 'u' when i + 4 < literal.Length - 1 && int.TryParse(literal.AsSpan(i + 1, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var code):
					value.Append((char)code);
					i += 4;
					break;
				case var other:
					value.Append(other);
					break;
			}
		}

		return value.ToString();
	}
}
