using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Nullean.Snapshots.Trees;

namespace Nullean.Snapshots.Json;

/// <summary>Parses, pretty-prints and compares JSON.</summary>
public static class JsonSnapshot
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

	/// <summary>The default format: key order ignored, numbers compared by value.</summary>
	public static TreeSnapshotFormat Format { get; } = new JsonSnapshotFormat(null);

	/// <summary>A format comparing with <paramref name="options"/>.</summary>
	public static TreeSnapshotFormat FormatWith(TreeDiffOptions? options) => options is null ? Format : new JsonSnapshotFormat(options);

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

	/// <summary>Compares two JSON texts as trees.</summary>
	public static SnapshotComparison Compare(string expected, string actual, TreeDiffOptions? options = null) =>
		FormatWith(options).Compare(expected, actual);

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

internal sealed class JsonSnapshotFormat(TreeDiffOptions? options) : TreeSnapshotFormat(options)
{
	public override string Name => "JSON";

	public override string Extension => "json";

	public override Node Parse(string text) => JsonSnapshot.Parse(text);
}
