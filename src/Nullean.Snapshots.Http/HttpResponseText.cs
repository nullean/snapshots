using System.Globalization;
using System.IO.Compression;
using System.Text;
using Nullean.Snapshots.Trees;

namespace Nullean.Snapshots.Http;

/// <summary>One response as the format writes it: its heading, status, headers and decoded body.</summary>
/// <param name="Heading">The text after <c>## </c>: the request as written, or method and path.</param>
/// <param name="StatusCode">The status code.</param>
/// <param name="ReasonPhrase">The reason phrase, if any.</param>
/// <param name="Headers">Every response and content header, a field per value.</param>
/// <param name="Body">The decoded body.</param>
/// <param name="MediaType">The media type of <c>Content-Type</c>, without parameters.</param>
/// <param name="CharSet">The charset of <c>Content-Type</c>.</param>
internal sealed record HttpResponseSection(
	string Heading,
	int StatusCode,
	string? ReasonPhrase,
	IReadOnlyList<KeyValuePair<string, string>> Headers,
	byte[] Body,
	string? MediaType,
	string? CharSet
);

/// <summary>Writes and parses the <c>.responses</c> text.</summary>
internal static class HttpResponseText
{
	/// <summary>Headers written unless left out: the ones that say what came back and where it points.</summary>
	internal static readonly string[] DefaultHeaders = ["content-type", "location"];

	/// <summary>Headers that carry credentials; never written, even when chosen.</summary>
	internal static readonly string[] NeverWritten = ["authorization", "cookie", "proxy-authorization", "set-cookie"];

	/// <summary>The default headers plus <paramref name="chosen"/>, lower case, without the credential headers.</summary>
	internal static HashSet<string> Chosen(IEnumerable<string>? chosen)
	{
		var set = new HashSet<string>(DefaultHeaders, StringComparer.Ordinal);
		foreach (var name in chosen ?? [])
		{
			if (!string.IsNullOrWhiteSpace(name))
				set.Add(name.Trim().ToLowerInvariant());
		}

		set.ExceptWith(NeverWritten);
		return set;
	}

	/// <summary>
	/// Appends <paramref name="section"/>, separated from what came before by a blank line. With <paramref name="shape"/>
	/// a JSON body is written as its <see cref="TreeShape"/> and a text body as its media type alone.
	/// </summary>
	internal static void Append(StringBuilder text, HttpResponseSection section, IReadOnlySet<string> chosen, bool shape = false)
	{
		if (text.Length > 0)
			text.Append('\n');
		text.Append("## ").Append(OneLine(section.Heading)).Append('\n');
		text.Append("status: ").Append(section.StatusCode.ToString(CultureInfo.InvariantCulture));
		if (!string.IsNullOrWhiteSpace(section.ReasonPhrase))
			text.Append(' ').Append(OneLine(section.ReasonPhrase.Trim()));
		text.Append('\n');

		foreach (var header in section
			.Headers
			.Select(h => (Name: h.Key.ToLowerInvariant(), h.Value))
			.Where(h => chosen.Contains(h.Name))
			.GroupBy(h => h.Name, StringComparer.Ordinal)
			.OrderBy(g => g.Key, StringComparer.Ordinal))
		{
			var value = OneLine(string.Join(", ", header.Select(h => h.Value.Trim())));
			text.Append(header.Key).Append(':');
			if (value.Length > 0)
				text.Append(' ').Append(value);
			text.Append('\n');
		}

		if (section.Body.Length == 0)
			return;
		text.Append('\n');
		AppendBody(text, section, shape);
	}

	private static void AppendBody(StringBuilder text, HttpResponseSection section, bool shape)
	{
		var kind = Classify(section.MediaType, section.CharSet);
		if (kind == BodyKind.Binary)
		{
			text.Append("binary: ").Append(section.Body.Length.ToString(CultureInfo.InvariantCulture)).Append(" bytes");
			if (section.MediaType is { Length: > 0 } mediaType)
				text.Append(", ").Append(OneLine(mediaType));
			text.Append('\n');
			return;
		}

		var body = Decode(section.Body, section.CharSet);
		if (kind == BodyKind.Json)
		{
			try
			{
				text.Append(shape ? JsonTree.Write(TreeShape.Project(JsonTree.Parse(body))) : JsonTree.Pretty(body));
				return;
			}
			catch (FormatException)
			{
				// Invalid JSON is still text: quoted below, so it can be read and compared.
			}
		}

		if (shape)
		{
			// Text is content, not structure: its media type is all a shape can say about it.
			text.Append("text: ").Append(OneLine(section.MediaType is { Length: > 0 } type ? type : "unknown")).Append('\n');
			return;
		}

		if (!shape && IsEventStream(section.MediaType) && Events(body) is { } events)
		{
			text.Append(events);
			return;
		}

		AppendQuoted(text, body);
	}

	private const string EventsMarker = "events:";

	private static readonly string[] EventFields = ["event", "id", "retry"];

	internal static bool IsEventStream(string? mediaType) => string.Equals(mediaType?.Trim(), "text/event-stream", StringComparison.OrdinalIgnoreCase);

	/// <summary>
	/// A server-sent event stream as <c>events:</c> and its events, separated by a blank line: <c>event</c>, <c>id</c> and <c>retry</c> as lines, and the <c>data</c> that is JSON pretty-printed
	/// on the lines after <c>data:</c>, else on its own line. <see langword="null"/> when the stream has anything this cannot say exactly (comments, a repeated field, data over
	/// several lines that is not JSON, empty data): it is quoted as text then.
	/// </summary>
	private static string? Events(string body)
	{
		var events = new List<string>();
		foreach (var block in Lines(body)
			.Aggregate(new List<List<string>> { new() }, (blocks, line) =>
			{
				if (line.Length == 0)
				{
					if (blocks[^1].Count > 0)
						blocks.Add([]);
				}
				else
					blocks[^1].Add(line);
				return blocks;
			})
			.Where(b => b.Count > 0))
		{
			var fields = new Dictionary<string, string>(StringComparer.Ordinal);
			var data = new List<string>();
			foreach (var line in block)
			{
				var colon = line.IndexOf(':', StringComparison.Ordinal);
				if (colon <= 0)
					return null;
				var name = line[..colon];
				var value = line[(colon + 1)..];
				if (value.StartsWith(' '))
					value = value[1..];
				if (name == "data")
					data.Add(value);
				else if (!EventFields.Contains(name, StringComparer.Ordinal) || !fields.TryAdd(name, value) || value.Length == 0 || value.Contains('\n'))
					return null;
			}

			if (data.Count == 0)
				return null;
			var payload = string.Join('\n', data);
			string? json = null;
			try
			{
				json = payload.Length > 0 ? JsonTree.Pretty(payload) : null;
			}
			catch (FormatException)
			{
				// Not JSON: kept as a line below.
			}

			if (json is null && (data.Count > 1 || payload.Length == 0))
				return null;
			var lines = new StringBuilder();
			foreach (var field in EventFields)
			{
				if (fields.TryGetValue(field, out var v))
					lines.Append(field).Append(": ").Append(v).Append('\n');
			}

			lines.Append(json is null ? "data: " + payload + "\n" : "data:\n" + json);
			events.Add(lines.ToString());
		}

		return events.Count == 0 ? null : EventsMarker + "\n" + string.Join('\n', events);
	}

	/// <summary>Every line prefixed with <c>&gt; </c> (an empty one with <c>&gt;</c>), so no body text can start a section.</summary>
	internal static void AppendQuoted(StringBuilder text, string body)
	{
		foreach (var line in Lines(body))
			text.Append(line.Length == 0 ? ">" : "> " + line).Append('\n');
	}

	internal static string[] Lines(string text) => text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n');

	private static string OneLine(string text) => text.Replace("\r", " ", StringComparison.Ordinal).Replace("\n", " ", StringComparison.Ordinal);

	internal enum BodyKind
	{
		Json,
		Text,
		Binary
	}

	/// <summary>
	/// JSON for <c>application/json</c> and <c>+json</c>; text for <c>text/*</c>, XML, JavaScript, YAML, other JSON
	/// types such as NDJSON, form posts and anything with a charset; binary otherwise.
	/// </summary>
	internal static BodyKind Classify(string? mediaType, string? charSet)
	{
		var type = mediaType?.Trim().ToLowerInvariant() ?? string.Empty;
		if (type == "application/json" || type.EndsWith("+json", StringComparison.Ordinal))
			return BodyKind.Json;
		if (
			type.StartsWith("text/", StringComparison.Ordinal) || type == "application/x-www-form-urlencoded" || type.Contains(
				"xml",
				StringComparison.Ordinal
			) || type.Contains("javascript", StringComparison.Ordinal) || type.Contains("ecmascript", StringComparison.Ordinal) || type.Contains(
				"json",
				StringComparison.Ordinal
			) || type.Contains("yaml", StringComparison.Ordinal) || !string.IsNullOrWhiteSpace(charSet)
		)
			return BodyKind.Text;
		return BodyKind.Binary;
	}

	/// <summary>The body as text, in its charset when that names a known encoding, else UTF-8.</summary>
	internal static string Decode(byte[] body, string? charSet)
	{
		var encoding = Encoding.UTF8;
		if (!string.IsNullOrWhiteSpace(charSet))
		{
			try
			{
				encoding = Encoding.GetEncoding(charSet.Trim().Trim('"'));
			}
			catch (ArgumentException)
			{
				// An unknown charset: UTF-8, as the parser's runner reads it.
			}
		}

		var text = encoding.GetString(body);
		return text.Length > 0 && text[0] == '﻿' ? text[1..] : text;
	}

	/// <summary>
	/// Undoes a <c>Content-Encoding</c> (gzip, deflate with or without the zlib wrapper, br), last applied first.
	/// Returns <see langword="null"/> for an encoding it does not know.
	/// </summary>
	internal static byte[]? Decompress(byte[] body, IEnumerable<string> encodings)
	{
		var applied = encodings.Select(e => e.Trim().ToLowerInvariant()).Where(e => e.Length > 0 && e != "identity").ToList();
		if (applied.Any(e => e is not ("gzip" or "x-gzip" or "deflate" or "br")))
			return null;
		for (var i = applied.Count - 1; i >= 0; i--)
		{
			body = applied[i] switch
			{
				"deflate" => Inflate(body),
				"br" => Copy(new BrotliStream(new MemoryStream(body), CompressionMode.Decompress)),
				_ => Copy(new GZipStream(new MemoryStream(body), CompressionMode.Decompress)),
			};
		}

		return body;
	}

	private static byte[] Inflate(byte[] body)
	{
		// HTTP's deflate is zlib-wrapped, but some servers send raw deflate.
		try
		{
			return Copy(new ZLibStream(new MemoryStream(body), CompressionMode.Decompress));
		}
		catch (InvalidDataException)
		{
			return Copy(new DeflateStream(new MemoryStream(body), CompressionMode.Decompress));
		}
	}

	private static byte[] Copy(Stream stream)
	{
		using (stream)
		{
			using var output = new MemoryStream();
			stream.CopyTo(output);
			return output.ToArray();
		}
	}

	/// <summary>
	/// Parses the text into a sequence of sections, <c>{request, status, headers, body | text | text-type | binary}</c>,
	/// with empty parts left out.
	/// </summary>
	/// <exception cref="FormatException">The text is not in this format; the message starts with the line.</exception>
	internal static SeqNode Parse(string text)
	{
		ArgumentNullException.ThrowIfNull(text);
		var lines = Lines(text);
		var sections = new List<Node>();
		var i = 0;
		while (i < lines.Length)
		{
			if (lines[i].Length == 0)
			{
				i++;
				continue;
			}

			var number = i + 1;
			if (!lines[i].StartsWith("## ", StringComparison.Ordinal))
				throw new FormatException($"Line {number}: a section starts with '## ' and the request.");
			var entries = new List<KeyValuePair<ScalarNode, Node>>
			{
				new(ScalarNode.String("request"), ScalarNode.String(lines[i][3..], new SourceSpan(number, 4))),
			};
			i++;
			if (i >= lines.Length || !lines[i].StartsWith("status: ", StringComparison.Ordinal))
				throw new FormatException($"Line {i + 1}: the section's second line is 'status: ' and the status code.");
			entries.Add(new(ScalarNode.String("status"), ScalarNode.String(lines[i][8..], new SourceSpan(i + 1, 9))));
			i++;

			var headers = new List<KeyValuePair<ScalarNode, Node>>();
			var names = new HashSet<string>(StringComparer.Ordinal);
			for (; i < lines.Length && lines[i].Length > 0 && !lines[i].StartsWith("## ", StringComparison.Ordinal); i++)
			{
				var colon = lines[i].IndexOf(':', StringComparison.Ordinal);
				if (colon <= 0)
					throw new FormatException($"Line {i + 1}: a header line is 'name: value'.");
				var name = lines[i][..colon];
				if (!names.Add(name))
					throw new FormatException($"Line {i + 1}: the header '{name}' appears twice in one section.");
				var value = lines[i][(colon + 1)..];
				var column = colon + 2;
				if (value.StartsWith(' '))
				{
					value = value[1..];
					column++;
				}

				headers.Add(new(ScalarNode.String(name, new SourceSpan(i + 1, 1)), ScalarNode.String(value, new SourceSpan(i + 1, column))));
			}

			if (headers.Count > 0)
				entries.Add(new(ScalarNode.String("headers"), new MapNode(headers, headers[0].Key.Span)));

			// The blank line before the body; when a heading follows it instead, the section has no body.
			while (i < lines.Length && lines[i].Length == 0)
				i++;
			var start = i;
			while (i < lines.Length && !lines[i].StartsWith("## ", StringComparison.Ordinal))
				i++;
			var end = i;
			while (end > start && lines[end - 1].Length == 0)
				end--;
			if (end > start)
				entries.Add(Body(lines[start..end], start + 1));

			sections.Add(new MapNode(entries, new SourceSpan(number, 1)));
		}

		return new SeqNode(sections);
	}

	private static KeyValuePair<ScalarNode, Node> Body(string[] body, int firstLine)
	{
		if (body[0] == EventsMarker)
			return new(ScalarNode.String("body"), ParseEvents(body, firstLine));
		if (body[0].StartsWith('>'))
		{
			var quoted = new List<string>(body.Length);
			for (var j = 0; j < body.Length; j++)
			{
				quoted.Add(body[j] switch
				{
					">" => string.Empty,
					var l when l.StartsWith("> ", StringComparison.Ordinal) => l[2..],
					_ => throw new FormatException($"Line {firstLine + j}: every line of a text body starts with '> '."),
				});
			}

			return new(ScalarNode.String("text"), ScalarNode.String(string.Join('\n', quoted), new SourceSpan(firstLine, 1)));
		}

		if (body[0].StartsWith("text: ", StringComparison.Ordinal))
		{
			if (body.Length > 1)
				throw new FormatException($"Line {firstLine + 1}: a text body in a shape is one line, 'text: {{media type}}'.");
			return new(ScalarNode.String("text-type"), ScalarNode.String(body[0][6..], new SourceSpan(firstLine, 7)));
		}

		if (body[0].StartsWith("binary: ", StringComparison.Ordinal))
		{
			if (body.Length > 1)
				throw new FormatException($"Line {firstLine + 1}: a binary body is one line, 'binary: {{n}} bytes, {{media type}}'.");
			return new(ScalarNode.String("binary"), ScalarNode.String(body[0][8..], new SourceSpan(firstLine, 9)));
		}

		Node json;
		try
		{
			json = JsonTree.Parse(string.Join('\n', body));
		}
		catch (FormatException e)
		{
			throw new FormatException($"Line {firstLine}: the body is not valid JSON, quoted text ('> ') or 'binary: '. {e.Message}", e);
		}

		return new(ScalarNode.String("body"), Shift(json, firstLine - 1));
	}

	/// <summary>The events of a stream, as written by <see cref="Events"/>: a sequence of <c>{event, id, retry, data}</c>, <c>data</c> a JSON tree or a string.</summary>
	private static SeqNode ParseEvents(string[] body, int firstLine)
	{
		var items = new List<Node>();
		var i = 1;
		while (i < body.Length)
		{
			if (body[i].Length == 0)
			{
				i++;
				continue;
			}

			var entries = new List<KeyValuePair<ScalarNode, Node>>();
			var span = new SourceSpan(firstLine + i, 1);
			for (; i < body.Length && body[i].Length > 0; i++)
			{
				var line = firstLine + i;
				var colon = body[i].IndexOf(':', StringComparison.Ordinal);
				if (colon <= 0)
					throw new FormatException($"Line {line}: an event line is 'event: ', 'id: ', 'retry: ' or 'data: '.");
				var name = body[i][..colon];
				var value = body[i][(colon + 1)..];
				if (value.StartsWith(' '))
					value = value[1..];
				if (name != "data" && !EventFields.Contains(name, StringComparer.Ordinal))
					throw new FormatException($"Line {line}: an event line is 'event: ', 'id: ', 'retry: ' or 'data: '.");
				if (entries.Any(e => e.Key.Canonical == name))
					throw new FormatException($"Line {line}: '{name}' appears twice in one event.");
				if (name != "data" || value.Length > 0)
				{
					entries.Add(new(ScalarNode.String(name), ScalarNode.String(value, new SourceSpan(line, colon + 3))));
					continue;
				}

				var end = i + 1;
				while (end < body.Length && body[end].Length > 0)
					end++;
				if (end == i + 1)
					throw new FormatException($"Line {line}: 'data:' is followed by JSON, or has its text after it.");
				Node json;
				try
				{
					json = JsonTree.Parse(string.Join('\n', body[(i + 1)..end]));
				}
				catch (FormatException e)
				{
					throw new FormatException($"Line {line + 1}: the data is not valid JSON. {e.Message}", e);
				}

				entries.Add(new(ScalarNode.String("data"), Shift(json, line)));
				i = end - 1;
			}

			items.Add(new MapNode(entries, span));
		}

		return new SeqNode(items, new SourceSpan(firstLine, 1));
	}

	/// <summary>Moves a tree parsed from the body alone to its lines in the whole text.</summary>
	private static Node Shift(Node node, int lines) => node switch
	{
		MapNode m =>
			new MapNode(
				[.. m.Entries.Select(e => new KeyValuePair<ScalarNode, Node>((ScalarNode)Shift(e.Key, lines), Shift(e.Value, lines)))],
				Shift(m.Span, lines)
			),
		SeqNode s => new SeqNode([.. s.Items.Select(n => Shift(n, lines))], Shift(s.Span, lines)),
		ScalarNode s => s with { Span = Shift(s.Span, lines) },
		_ => node,
	};

	private static SourceSpan? Shift(SourceSpan? span, int lines) => span is { } s ? s with { Line = s.Line + lines } : null;
}
