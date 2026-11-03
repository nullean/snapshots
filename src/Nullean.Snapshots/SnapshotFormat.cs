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
public sealed record SnapshotComparison(bool IsMatch, IReadOnlyList<IReadOnlyList<string>> Differences)
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
