using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace Nullean.Snapshots.Trees;

/// <summary>
/// JSON to <see cref="Node"/> trees, and the form JSON snapshots are stored in. Internal to the snapshot packages: the
/// JSON format and the HTTP format both use it, and the HTTP format must not need AwesomeAssertions.
/// </summary>
internal static class JsonTree
{
	private static readonly JsonReaderOptions ReaderOptions = new() { MaxDepth = 256 };

	private static readonly JsonWriterOptions WriterOptions = new()
	{
		Indented = true,
		IndentSize = 2,
		NewLine = "\n",
		// Snapshots are read by people: keep non-ASCII text and characters like < and ' as they are.
		Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
		MaxDepth = 256,
	};

	/// <summary>
	/// Parses JSON into a <see cref="Node"/> tree with line and column positions. Numbers keep their text, so big
	/// integers survive.
	/// </summary>
	/// <exception cref="FormatException">The text is not valid JSON, or an object repeats a property name.</exception>
	public static Node Parse(string json)
	{
		ArgumentNullException.ThrowIfNull(json);
		var bytes = Encoding.UTF8.GetBytes(json);
		var positions = new Positions(bytes);
		var reader = new Utf8JsonReader(bytes, ReaderOptions);
		try
		{
			if (!reader.Read())
				throw new FormatException("The text is empty.");
			var node = ReadValue(ref reader, positions);
			if (reader.Read())
				throw new FormatException($"Unexpected content after the value at {positions.At(reader.TokenStartIndex)}.");
			return node;
		}
		catch (JsonException e)
		{
			throw new FormatException(e.Message, e);
		}
	}

	/// <summary>
	/// The form JSON snapshots are stored in: indented by two spaces, properties in their original order, numbers as
	/// written, non-ASCII text unescaped, ending in one newline.
	/// </summary>
	/// <exception cref="FormatException">The text is not valid JSON, or an object repeats a property name.</exception>
	public static string Pretty(string json)
	{
		_ = Parse(json);
		using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 256 });
		using var buffer = new MemoryStream();
		using (var writer = new Utf8JsonWriter(buffer, WriterOptions))
			document.WriteTo(writer);
		return SnapshotText.Normalize(Encoding.UTF8.GetString(buffer.ToArray()));
	}

	/// <summary>
	/// A tree in the same form as <see cref="Pretty"/>: two-space indent, properties in the tree's order, numbers as
	/// their text, ending in one newline.
	/// </summary>
	public static string Write(Node node)
	{
		ArgumentNullException.ThrowIfNull(node);
		using var buffer = new MemoryStream();
		using (var writer = new Utf8JsonWriter(buffer, WriterOptions))
			WriteValue(writer, node);
		return SnapshotText.Normalize(Encoding.UTF8.GetString(buffer.ToArray()));
	}

	private static void WriteValue(Utf8JsonWriter writer, Node node)
	{
		switch (node)
		{
			case MapNode map:
				writer.WriteStartObject();
				foreach (var entry in map.Entries)
				{
					writer.WritePropertyName(entry.Key.Canonical);
					WriteValue(writer, entry.Value);
				}

				writer.WriteEndObject();
				break;
			case SeqNode seq:
				writer.WriteStartArray();
				foreach (var item in seq.Items)
					WriteValue(writer, item);
				writer.WriteEndArray();
				break;
			case ScalarNode { Kind: ScalarKind.String } s:
				writer.WriteStringValue(s.Canonical);
				break;
			case ScalarNode { Kind: ScalarKind.Bool } s:
				writer.WriteBooleanValue(s.Canonical == "true");
				break;
			case ScalarNode { Kind: ScalarKind.Null }:
				writer.WriteNullValue();
				break;
			case ScalarNode s:
				writer.WriteRawValue(s.Canonical);
				break;
			default:
				throw new ArgumentException($"Unknown node type {node.GetType().Name}.", nameof(node));
		}
	}

	private static Node ReadValue(ref Utf8JsonReader reader, Positions positions)
	{
		var span = positions.At(reader.TokenStartIndex);
		switch (reader.TokenType)
		{
			case JsonTokenType.StartObject:
				var entries = new List<KeyValuePair<ScalarNode, Node>>();
				var names = new HashSet<string>(StringComparer.Ordinal);
				while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
				{
					var keySpan = positions.At(reader.TokenStartIndex);
					var name = reader.GetString()!;
					if (!names.Add(name))
						throw new FormatException($"The property {NodePath.Quote(name)} appears twice in one object, at {keySpan}.");
					reader.Read();
					entries.Add(new(ScalarNode.String(name, keySpan), ReadValue(ref reader, positions)));
				}

				return new MapNode(entries, span);
			case JsonTokenType.StartArray:
				var items = new List<Node>();
				while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
					items.Add(ReadValue(ref reader, positions));
				return new SeqNode(items, span);
			case JsonTokenType.String:
				return ScalarNode.String(reader.GetString()!, span);
			case JsonTokenType.Number:
				var text = Encoding.UTF8.GetString(reader.ValueSpan);
				var isFloat = text.AsSpan().IndexOfAny('.', 'e', 'E') >= 0;
				return new ScalarNode(isFloat ? ScalarKind.Float : ScalarKind.Int, text, span);
			case JsonTokenType.True:
				return ScalarNode.Bool(true, span);
			case JsonTokenType.False:
				return ScalarNode.Bool(false, span);
			case JsonTokenType.Null:
				return ScalarNode.Null(span);
			default:
				throw new FormatException($"Unexpected {reader.TokenType} at {span}.");
		}
	}

	/// <summary>Turns byte offsets into 1-based line and column, counting columns in characters, not bytes.</summary>
	private sealed class Positions(byte[] bytes)
	{
		private readonly List<int> _lineStarts = LineStarts(bytes);

		public SourceSpan At(long offset)
		{
			var index = _lineStarts.BinarySearch((int)offset);
			var line = index >= 0 ? index : ~index - 1;
			var column = 1;
			for (var i = _lineStarts[line]; i < offset; i++)
			{
				if ((bytes[i] & 0xC0) != 0x80)
					column++;
			}

			return new SourceSpan(line + 1, column);
		}

		private static List<int> LineStarts(byte[] bytes)
		{
			var starts = new List<int> { 0 };
			for (var i = 0; i < bytes.Length; i++)
			{
				if (bytes[i] == (byte)'\n')
					starts.Add(i + 1);
			}

			return starts;
		}
	}
}
