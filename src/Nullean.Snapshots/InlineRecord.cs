using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace Nullean.Snapshots;

/// <summary>
/// The received file of an inline snapshot (<c>X.L42.received.inline</c>): JSON naming the source file and call, the
/// literal's value when the test ran and the value to write. <c>snap accept</c> turns it into an edit of the literal.
/// </summary>
/// <param name="Source">The test file, relative to the received folder, with <c>/</c> separators.</param>
/// <param name="Format">The snapshot format's name, for display.</param>
/// <param name="Edit">What to rewrite.</param>
/// <param name="SourceHash">
/// <see cref="InlineSnapshotSource.Hash"/> of the test file when the test ran, or null when it could not be read then.
/// </param>
internal sealed record InlineRecord(string Source, string Format, InlineEdit Edit, string? SourceHash)
{
	/// <summary>The JSON text of a record, indented, with LF line endings and a trailing newline.</summary>
	public static string Render(string source, string format, InlineEdit edit, string? sourceHash)
	{
		using var buffer = new MemoryStream();
		using (var json = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true, NewLine = "\n", Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
		{
			json.WriteStartObject();
			json.WriteString("format", format);
			json.WriteString("method", edit.MethodName);
			json.WriteString("source", source);
			json.WriteString("sourceHash", sourceHash);
			json.WriteNumber("line", edit.Line);
			json.WriteString("expression", edit.Expression);
			json.WriteString("expected", edit.Expected);
			json.WriteString("actual", edit.NewValue);
			json.WriteEndObject();
		}

		return Encoding.UTF8.GetString(buffer.ToArray()) + "\n";
	}

	/// <summary>Reads a record; a file that is not one is a <see cref="FormatException"/>.</summary>
	public static InlineRecord Parse(string text)
	{
		try
		{
			using var document = JsonDocument.Parse(text);
			var root = document.RootElement;
			return new InlineRecord(
				Required(root, "source"),
				Required(root, "format"),
				new InlineEdit(
					Required(root, "method"),
					root.GetProperty("line").GetInt32(),
					Optional(root, "expression"),
					Optional(root, "expected"),
					Required(root, "actual")),
				Optional(root, "sourceHash"));
		}
		catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
		{
			throw new FormatException("not an inline snapshot record: " + e.Message, e);
		}
	}

	private static string Required(JsonElement root, string name) =>
		root.GetProperty(name).GetString() ?? throw new FormatException($"'{name}' is null");

	private static string? Optional(JsonElement root, string name) =>
		root.TryGetProperty(name, out var value) && value.ValueKind != JsonValueKind.Null ? value.GetString() : null;
}
