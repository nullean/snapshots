using System.Collections;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Numerics;
using System.Reflection;
using System.Text;

namespace Nullean.Snapshots.Objects;

/// <summary>
/// Dumps an object graph as YAML by reflection. Public instance properties and fields are written; sets are sorted by what they
/// render, so hash order does not matter; a reference back to an object on the path is written as <c>&lt;cycle to $.Path&gt;</c>; a
/// property that throws is written as <c>&lt;throws ExceptionType&gt;</c>, without its message, which differs between operating systems.
/// </summary>
public static class ObjectSnapshot
{
	internal const string ReflectionReason =
		"Reads the object's public members by reflection. Under trimming or native AOT the members of types that are only reached through other objects may be missing from the dump.";

	/// <summary>The YAML text for <paramref name="value"/>.</summary>
	/// <typeparam name="T">The declared type. Its public properties and fields are kept by the trimmer; the types nested in it are not.</typeparam>
	/// <param name="value">What to dump.</param>
	/// <param name="options">How to read it.</param>
	[RequiresUnreferencedCode(ReflectionReason)]
	public static string Dump<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties | DynamicallyAccessedMemberTypes.PublicFields)] T>(
		T value,
		ObjectDumpOptions? options = null
	) => ToText(new Dumper(options ?? new ObjectDumpOptions(), typeof(T)).Build(value, "$", 0));

	private abstract record DocNode;

	private sealed record DocScalar(string Text) : DocNode;

	private sealed record DocMap(List<KeyValuePair<string, DocNode>> Entries) : DocNode;

	private sealed record DocSeq(List<DocNode> Items) : DocNode;

	[RequiresUnreferencedCode(ReflectionReason)]
	private sealed class Dumper(ObjectDumpOptions options, Type rootType)
	{
		private readonly Dictionary<object, string> _onPath = [with(ReferenceEqualityComparer.Instance)];

		public DocNode Build(object? value, string path, int depth)
		{
			if (value is null)
				return new DocScalar("null");
			if (options.TryRender(value) is { } rendered)
				return new DocScalar(Quote(rendered));
			if (Scalar(value) is { } scalar)
				return new DocScalar(scalar);
			if (depth >= options.MaxDepth)
				return new DocScalar(Quote("<max depth>"));
			if (_onPath.TryGetValue(value, out var target))
				return new DocScalar(Quote($"<cycle to {target}>"));

			_onPath[value] = path;
			try
			{
				return value switch
				{
					IDictionary dictionary => Dictionary(dictionary, path, depth),
					IEnumerable items => Sequence(items, path, depth),
					_ => Members(value, path, depth),
				};
			}
			finally
			{
				_onPath.Remove(value);
			}
		}

		private static string? Scalar(object value) => value switch
		{
			string s => Quote(s),
			bool b => b ? "true" : "false",
			char c => Quote(c.ToString()),
			Enum e => Quote(e.ToString()),
			Type t => Quote(t.ToString()),
			Delegate d => Quote($"<delegate {d.GetType()}>"),
			DateTime d => Quote(d.ToString("O", CultureInfo.InvariantCulture)),
			DateTimeOffset d => Quote(d.ToString("O", CultureInfo.InvariantCulture)),
			Uri u => Quote(u.OriginalString),
			float f => f.ToString("R", CultureInfo.InvariantCulture),
			double d => d.ToString("R", CultureInfo.InvariantCulture),
			decimal or BigInteger => ((IFormattable)value).ToString(null, CultureInfo.InvariantCulture),
			_ when value.GetType().IsPrimitive => ((IFormattable)value).ToString(null, CultureInfo.InvariantCulture),
			IFormattable f => Quote(f.ToString(null, CultureInfo.InvariantCulture)),
			_ => null,
		};

		private DocMap Dictionary(IDictionary dictionary, string path, int depth)
		{
			var entries = new List<KeyValuePair<string, DocNode>>();
			foreach (DictionaryEntry entry in dictionary)
			{
				var key = Convert.ToString(entry.Key, CultureInfo.InvariantCulture) ?? "null";
				entries.Add(new(key, Build(entry.Value, path + Key(key), depth + 1)));
			}

			entries.Sort(static (a, b) => string.CompareOrdinal(a.Key, b.Key));
			return new DocMap(entries);
		}

		private DocSeq Sequence(IEnumerable items, string path, int depth)
		{
			var nodes = new List<DocNode>();
			var index = 0;
			foreach (var item in items)
			{
				if (index == options.MaxItems)
				{
					nodes.Add(new DocScalar(Quote("<+more>")));
					break;
				}

				nodes.Add(Build(item, $"{path}[{index.ToString(CultureInfo.InvariantCulture)}]", depth + 1));
				index++;
			}

			if (options.IsSetType(items.GetType()))
				nodes = [.. nodes.OrderBy(ToText, StringComparer.Ordinal)];
			return new DocSeq(nodes);
		}

		private DocMap Members(object value, string path, int depth)
		{
			var type = value.GetType();
			var entries = new List<KeyValuePair<string, DocNode>>();
			var members = type.GetMembers(BindingFlags.Public | BindingFlags.Instance);
			if (options.RequireIncludedTypes && !members.Any(m => m is PropertyInfo or FieldInfo) && type != rootType && !options.IsIncluded(type))
				throw new InvalidOperationException(
					$"{type} has no public members to read. Under trimming or native AOT its members were probably removed: add .Include<{type.Name}>() to the ObjectDumpOptions."
				);
			foreach (var member in members)
			{
				Type memberType;
				Func<object> read;
				switch (member)
				{
					case PropertyInfo { CanRead: true } p when p.GetIndexParameters().Length == 0:
						memberType = p.PropertyType;
						read = () => p.GetValue(value)!;
						break;
					case FieldInfo f:
						memberType = f.FieldType;
						read = () => f.GetValue(value)!;
						break;
					default:
						continue;
				}

				if (options.IsIgnoredMember(member.Name) || options.IsIgnoredType(memberType))
					continue;
				DocNode node;
				try
				{
					node = Build(read(), path + Key(member.Name), depth + 1);
				}
				catch (TargetInvocationException e)
				{
					node = new DocScalar(Quote($"<throws {(e.InnerException ?? e).GetType().Name}>"));
				}

				entries.Add(new(member.Name, node));
			}

			return new DocMap(entries);
		}

		private static string Key(string key) => IsIdentifier(key) ? "." + key : "[" + Quote(key) + "]";
	}

	private static bool IsIdentifier(string key) =>
		key.Length > 0 && (char.IsAsciiLetter(key[0]) || key[0] == '_') && key.All(c => char.IsAsciiLetterOrDigit(c) || c == '_') && !IsYamlWord(key);

	private static bool IsYamlWord(string key) => key.ToLowerInvariant() is "null" or "true" or "false" or "yes" or "no" or "on" or "off" or "y" or "n";

	private static string Quote(string text)
	{
		var quoted = new StringBuilder(text.Length + 2).Append('"');
		foreach (var c in text)
		{
			switch (c)
			{
				case '\\':
					quoted.Append("\\\\");
					break;
				case '"':
					quoted.Append("\\\"");
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
				case < ' ' or (>= '\u007f' and <= '\u0085') or '\u2028' or '\u2029' or '\uFEFF':
					quoted.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
					break;
				default:
					quoted.Append(c);
					break;
			}
		}

		return quoted.Append('"').ToString();
	}

	private static string ToText(DocNode root)
	{
		var lines = new List<string>();
		Emit(root, 0, lines);
		return string.Join('\n', lines) + "\n";
	}

	private static void Emit(DocNode node, int indent, List<string> lines)
	{
		var pad = new string(' ', indent);
		switch (node)
		{
			case DocScalar s:
				lines.Add(pad + s.Text);
				break;
			case DocMap { Entries.Count: 0 }:
				lines.Add(pad + "{}");
				break;
			case DocSeq { Items.Count: 0 }:
				lines.Add(pad + "[]");
				break;
			case DocMap map:
				foreach (var (key, value) in map.Entries)
				{
					var name = IsIdentifier(key) ? key : Quote(key);
					if (value is DocScalar or DocMap { Entries.Count: 0 } or DocSeq { Items.Count: 0 })
					{
						var inline = new List<string>();
						Emit(value, 0, inline);
						lines.Add($"{pad}{name}: {inline[0]}");
					}
					else
					{
						lines.Add($"{pad}{name}:");
						Emit(value, indent + 2, lines);
					}
				}

				break;
			case DocSeq seq:
				foreach (var item in seq.Items)
				{
					var start = lines.Count;
					Emit(item, indent + 2, lines);
					// "- " takes the place of the first two columns of the item's first line, so a map's first entry sits on the dash line.
					lines[start] = item is DocSeq { Items.Count: > 0 } ? pad + "-\n" + lines[start] : pad + "- " + lines[start][(indent + 2)..];
				}

				break;
			default:
				throw new InvalidOperationException("Unknown node.");
		}
	}
}
