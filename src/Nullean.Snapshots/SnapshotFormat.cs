namespace Nullean.Snapshots;

/// <summary>
/// How one kind of snapshot is stored and compared: its display name, its file extension, and how two normalised
/// texts are compared and described.
/// </summary>
public abstract class SnapshotFormat
{
	/// <summary>The name used in failure messages, e.g. <c>Text</c> or <c>JSON</c>.</summary>
	public abstract string Name { get; }

	/// <summary>The file extension without a dot, e.g. <c>txt</c>.</summary>
	public abstract string Extension { get; }

	/// <summary>
	/// Compares the verified text with the actual text. Both are already scrubbed and normalised
	/// (<see cref="SnapshotText.Normalize"/>).
	/// </summary>
	public abstract SnapshotComparison Compare(string verified, string actual);
}

/// <summary>The result of comparing a verified snapshot with an actual value.</summary>
/// <param name="IsMatch">Whether the two are considered equal.</param>
/// <param name="Differences">
/// One entry per difference, each the lines that describe it. The failure message shows them in order and applies the
/// caps, so a format can return every difference it finds.
/// </param>
/// <param name="LineDiff">
/// Optional unified hunks (<see cref="LineDiff.Hunks"/>) shown after the differences, for formats whose differences
/// are structural. They share the line cap.
/// </param>
/// <param name="Summary">
/// Optional line shown before the differences, for formats whose differences need an overall measure, such as how many
/// pixels differ.
/// </param>
public sealed record SnapshotComparison(
	bool IsMatch,
	IReadOnlyList<IReadOnlyList<string>> Differences,
	IReadOnlyList<IReadOnlyList<string>>? LineDiff = null,
	string? Summary = null
)
{
	/// <summary>A comparison that found no differences.</summary>
	public static SnapshotComparison Match { get; } = new(true, []);
}

/// <summary>The formats that ship with the core package.</summary>
public static class SnapshotFormats
{
	/// <summary>Plain text, compared line by line and described as unified hunks.</summary>
	public static SnapshotFormat Text { get; } = new TextSnapshotFormat();
}

internal sealed class TextSnapshotFormat : SnapshotFormat
{
	public override string Name => "Text";

	public override string Extension => "txt";

	public override SnapshotComparison Compare(string verified, string actual) =>
		string.Equals(verified, actual, StringComparison.Ordinal)
			? SnapshotComparison.Match
			: new SnapshotComparison(false, LineDiff.Hunks(verified, actual, LineDiff.DefaultContext));
}

/// <summary>
/// A snapshot stored as bytes, such as an image: compared by the format, never scrubbed or normalised, and written
/// exactly as given.
/// </summary>
public abstract class BinarySnapshotFormat
{
	/// <summary>The name used in failure messages, e.g. <c>Image</c>.</summary>
	public abstract string Name { get; }

	/// <summary>The file extension without a dot, e.g. <c>png</c>.</summary>
	public abstract string Extension { get; }

	/// <summary>
	/// The suffixes of every attachment <see cref="Compare"/> can return, e.g. <c>diff.png</c>. A passing or accepted
	/// snapshot deletes stale ones, so a format declares them all up front.
	/// </summary>
	public virtual IReadOnlyList<string> AttachmentSuffixes => [];

	/// <summary>
	/// Throws a <see cref="FormatException"/> when <paramref name="actual"/> is not a valid value of this format, before
	/// anything is written. The default accepts everything.
	/// </summary>
	public virtual void Validate(ReadOnlySpan<byte> actual) { }

	/// <summary>Compares the verified bytes with the actual bytes.</summary>
	public abstract BinarySnapshotComparison Compare(ReadOnlySpan<byte> verified, ReadOnlySpan<byte> actual);
}

/// <summary>The result of comparing binary snapshots, and the files that help a reviewer see why they differ.</summary>
/// <param name="Comparison">Whether they match and how they differ, as for text.</param>
/// <param name="Attachments">Written next to the received file when the comparison fails.</param>
public sealed record BinarySnapshotComparison(SnapshotComparison Comparison, IReadOnlyList<SnapshotAttachment> Attachments)
{
	/// <summary>A comparison that found no differences.</summary>
	public static BinarySnapshotComparison Match { get; } = new(SnapshotComparison.Match, []);
}

/// <summary>A file written next to a received snapshot, such as a diff image.</summary>
/// <param name="Label">Shown in the failure message before its path, e.g. <c>Diff</c>.</param>
/// <param name="Suffix">
/// One of the format's <see cref="BinarySnapshotFormat.AttachmentSuffixes"/>: the file is
/// <c>{name}.received-{suffix}</c>, e.g. <c>RendersChart.received-diff.png</c>.
/// </param>
/// <param name="Content">The file's bytes.</param>
public sealed record SnapshotAttachment(string Label, string Suffix, ReadOnlyMemory<byte> Content);
