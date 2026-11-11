using System.Text;
using Nullean.Snapshots.Trees;

namespace Nullean.Snapshots.Browser;

/// <summary>
/// Reads and writes accessibility trees as Nullean.Cdp prints them: one node per line,
/// <c>- role "name" [level=1, checked=true, selected]: value</c>, two spaces of indent per level.
/// </summary>
/// <remarks>
/// A node becomes a map of <c>role</c>, and when present <c>name</c>, <c>attrs</c> (a map; a bare flag is
/// <c>true</c>, anything else a string), <c>value</c> and <c>children</c>. Leaving empty parts out keeps paths in
/// differences short: <c>$[0].children[3].name</c>.
/// </remarks>
public static class AriaTree
{
	/// <summary>Parses a printed tree. <paramref name="firstLine"/> numbers positions when the tree sits in a larger file.</summary>
	/// <exception cref="FormatException">A line is not a node, or is indented more than one level below its parent.</exception>
	public static SeqNode Parse(string text, int firstLine = 1)
	{
		ArgumentNullException.ThrowIfNull(text);
		var lines = text.Split('\n');
		var roots = new List<Node>();
		// The children being collected at each depth, and the node each list belongs to.
		var stack = new List<(Dictionary<string, Node> Node, List<Node> Children, SourceSpan Span)>();
		for (var i = 0; i < lines.Length; i++)
		{
			var line = lines[i].TrimEnd('\r');
			if (line.Length == 0 && i == lines.Length - 1)
				break;
			var number = firstLine + i;
			var indent = 0;
			while (indent < line.Length && line[indent] == ' ')
				indent++;
			if (indent % 2 != 0 || indent / 2 > stack.Count)
				throw new FormatException($"Line {number}: indented {indent} spaces, which is not a level below the line before.");
			if (!line.AsSpan(indent).StartsWith("- "))
				throw new FormatException($"Line {number}: an accessibility tree line starts with '- '.");

			var depth = indent / 2;
			while (stack.Count > depth)
				Close(stack, roots);
			var (fields, span) = ParseNode(line, indent + 2, number);
			stack.Add((fields, [], span));
		}

		while (stack.Count > 0)
			Close(stack, roots);
		return new SeqNode(roots, roots.Count > 0 ? ((MapNode)roots[0]).Span : null);
	}

	private static void Close(List<(Dictionary<string, Node> Node, List<Node> Children, SourceSpan Span)> stack, List<Node> roots)
	{
		var (fields, children, span) = stack[^1];
		stack.RemoveAt(stack.Count - 1);
		var entries = new List<KeyValuePair<ScalarNode, Node>>();
		foreach (var key in Keys)
		{
			if (fields.TryGetValue(key, out var value))
				entries.Add(new(ScalarNode.String(key), value));
		}

		if (children.Count > 0)
			entries.Add(new(ScalarNode.String("children"), new SeqNode(children, ((MapNode)children[0]).Span)));
		var node = new MapNode(entries, span);
		(stack.Count > 0 ? stack[^1].Children : roots).Add(node);
	}

	private static readonly string[] Keys = ["role", "name", "attrs", "value"];

	private static (Dictionary<string, Node> Fields, SourceSpan Span) ParseNode(string line, int start, int number)
	{
		var span = new SourceSpan(number, start - 1);
		var fields = new Dictionary<string, Node>(StringComparer.Ordinal);
		var i = start;
		var roleEnd = i;
		while (roleEnd < line.Length && line[roleEnd] != ' ' && !(line[roleEnd] == ':' && roleEnd + 1 < line.Length && line[roleEnd + 1] == ' '))
			roleEnd++;
		if (roleEnd == i)
			throw new FormatException($"Line {number}: the node has no role.");
		fields["role"] = ScalarNode.String(line[i..roleEnd], span);
		i = roleEnd;

		if (Next(line, i, " \""))
		{
			var name = new StringBuilder();
			var closed = false;
			for (i += 2; i < line.Length; i++)
			{
				var c = line[i];
				if (c == '"')
				{
					closed = true;
					i++;
					break;
				}

				if (c == '\\' && i + 1 < line.Length)
				{
					name.Append(line[++i] switch
					{
						'n' => '\n',
						'r' => '\r',
						't' => '\t',
						var other => other,
					});
				}
				else
					name.Append(c);
			}

			if (!closed)
				throw new FormatException($"Line {number}: the name's quote is not closed.");
			fields["name"] = ScalarNode.String(name.ToString(), new SourceSpan(number, roleEnd + 2));
		}

		if (Next(line, i, " ["))
		{
			var close = line.IndexOf(']', i);
			if (close < 0)
				throw new FormatException($"Line {number}: the attribute list is not closed.");
			var attrs = new List<KeyValuePair<ScalarNode, Node>>();
			var attrSpan = new SourceSpan(number, i + 2);
			foreach (var attr in line[(i + 2)..close].Split(", "))
			{
				var equals = attr.IndexOf('=', StringComparison.Ordinal);
				if (attr.Length == 0 || equals == 0)
					throw new FormatException($"Line {number}: an attribute has no name.");
				attrs.Add(
					equals < 0
						? new(ScalarNode.String(attr), ScalarNode.Bool(true, attrSpan))
						: new(ScalarNode.String(attr[..equals]), ScalarNode.String(attr[(equals + 1)..], attrSpan))
				);
			}

			fields["attrs"] = new MapNode(attrs, attrSpan);
			i = close + 1;
		}

		if (Next(line, i, ": "))
		{
			fields["value"] = ScalarNode.String(line[(i + 2)..], new SourceSpan(number, i + 3));
			i = line.Length;
		}

		if (i != line.Length)
			throw new FormatException($"Line {number}: unexpected text after the node: '{line[i..]}'.");
		return (fields, span);
	}

	private static bool Next(string line, int i, string text) => line.AsSpan(i).StartsWith(text, StringComparison.Ordinal);

	/// <summary>Prints a tree that <see cref="Parse"/> returns (positions are ignored).</summary>
	public static string Print(SeqNode tree)
	{
		ArgumentNullException.ThrowIfNull(tree);
		var text = new StringBuilder();
		foreach (var node in tree.Items)
			Print(text, (MapNode)node, 0);
		return text.ToString();
	}

	private static void Print(StringBuilder text, MapNode node, int depth)
	{
		Node? Field(string key) => node.Entries.FirstOrDefault(e => e.Key.Canonical == key).Value;

		text.Append(' ', depth * 2).Append("- ").Append(((ScalarNode)Field("role")!).Canonical);
		if (Field("name") is ScalarNode name)
		{
			text.Append(" \"");
			foreach (var c in name.Canonical)
			{
				_ = c switch
				{
					'"' => text.Append("\\\""),
					'\\' => text.Append("\\\\"),
					'\n' => text.Append("\\n"),
					'\r' => text.Append("\\r"),
					'\t' => text.Append("\\t"),
					_ => text.Append(c),
				};
			}

			text.Append('"');
		}

		if (Field("attrs") is MapNode attrs)
		{
			text.Append(" [");
			text.AppendJoin(
				", ",
				attrs.Entries.Select(
					a => a.Value is ScalarNode { Kind: ScalarKind.Bool } ? a.Key.Canonical : $"{a.Key.Canonical}={((ScalarNode)a.Value).Canonical}"
				)
			);
			text.Append(']');
		}

		if (Field("value") is ScalarNode value)
			text.Append(": ").Append(value.Canonical);
		text.Append('\n');
		if (Field("children") is SeqNode children)
		{
			foreach (var child in children.Items)
				Print(text, (MapNode)child, depth + 1);
		}
	}
}
