using Nullean.Snapshots.Trees;

namespace Nullean.Snapshots.Json;

/// <summary>Parses, pretty-prints and compares JSON.</summary>
public static class JsonSnapshot
{
	/// <summary>The default format: key order ignored, numbers compared by value.</summary>
	public static TreeSnapshotFormat Format { get; } = new JsonSnapshotFormat(null);

	/// <summary>A format comparing with <paramref name="options"/>.</summary>
	public static TreeSnapshotFormat FormatWith(TreeDiffOptions? options) => options is null ? Format : new JsonSnapshotFormat(options);

	/// <summary>
	/// Parses JSON into a <see cref="Node"/> tree with line and column positions. Numbers keep their text, so big
	/// integers survive.
	/// </summary>
	/// <exception cref="FormatException">The text is not valid JSON, or an object repeats a property name.</exception>
	public static Node Parse(string json) => JsonTree.Parse(json);

	/// <summary>
	/// The form JSON snapshots are stored in: indented by two spaces, properties in their original order, numbers as
	/// written, non-ASCII text unescaped, ending in one newline.
	/// </summary>
	/// <exception cref="FormatException">The text is not valid JSON, or an object repeats a property name.</exception>
	public static string Pretty(string json) => JsonTree.Pretty(json);

	/// <summary>Compares two JSON texts as trees.</summary>
	public static SnapshotComparison Compare(string expected, string actual, TreeDiffOptions? options = null) =>
		FormatWith(options).Compare(expected, actual);
}

internal sealed class JsonSnapshotFormat(TreeDiffOptions? options) : TreeSnapshotFormat(options)
{
	public override string Name => "JSON";

	public override string Extension => "json";

	public override Node Parse(string text) => JsonSnapshot.Parse(text);
}
