using System.Globalization;
using System.Text;

namespace Nullean.Snapshots;

/// <summary>The caps that keep a failure message readable.</summary>
public static class SnapshotMessageLimits
{
	/// <summary>At most this many differences are listed.</summary>
	public const int MaxDifferences = 20;

	/// <summary>At most this many lines describe them.</summary>
	public const int MaxLines = 80;
}

/// <summary>Everything a failure message is built from. Paths are repository-relative with <c>/</c> separators.</summary>
internal sealed record SnapshotFailure(
	string FormatName,
	string VerifiedPath,
	string ReceivedPath,
	string Key,
	SnapshotComparison? Comparison,
	string? Because,
	string ProjectPath,
	string? TreeNodeFilter,
	bool AcceptIgnoredInCi
)
{
	/// <summary>Further received files to point at, such as a diff image: a label and a repository-relative path.</summary>
	public IReadOnlyList<(string Label, string Path)> Attachments { get; init; } = [];

	/// <summary>Set for an inline snapshot: it has no verified file, its expected value is in the test's source.</summary>
	public InlineFailure? Inline { get; init; }

	/// <summary>True when there was no verified file to compare with.</summary>
	public bool IsNew => Comparison is null;

	/// <summary>
	/// Renders the message. Content is only ever appended, never used as a format string, so braces in a snapshot are
	/// safe.
	/// </summary>
	public string Render()
	{
		var message = new StringBuilder();
		var because = Because is { Length: > 0 } b ? " " + b : string.Empty;
		if (Inline is { } inline)
		{
			if (Comparison is null)
			{
				message.Append(FormatName).Append(" inline snapshot is new").Append(because).Append(": ").Append(inline.Method)
					.Append("() at ").Append(inline.Location).Append(" has no expected value\n");
			}
			else
			{
				message.Append(FormatName).Append(" did not match inline snapshot").Append(because).Append(": ").Append(inline.Location).Append('\n');
				AppendComparison(message, Comparison);
			}

			if (inline.Note is { Length: > 0 } note)
				message.Append(note).Append('\n');
		}
		else if (Comparison is null)
		{
			message.Append(FormatName).Append(" snapshot is new").Append(because).Append(": no verified file at ").Append(VerifiedPath).Append('\n');
		}
		else
		{
			message.Append(FormatName).Append(" did not match snapshot").Append(because).Append(": ").Append(VerifiedPath).Append('\n');
			AppendComparison(message, Comparison);
		}

		message.Append("Received: ").Append(ReceivedPath).Append('\n');
		foreach (var (label, path) in Attachments)
			message.Append((label + ":").PadRight(10)).Append(path).Append('\n');
		message.Append("Accept:   snap accept ").Append(Key).Append("   (or: dnx Nullean.Snapshots.Cli accept ").Append(Key).Append(")\n");
		if (TreeNodeFilter is not null)
		{
			message.Append("   or:    ").Append(SnapshotEnvironment.AcceptVariable).Append("=1 dotnet test --project ").Append(ProjectPath)
				.Append(" --treenode-filter \"").Append(TreeNodeFilter).Append("\"\n");
		}

		if (AcceptIgnoredInCi)
		{
			message.Append(SnapshotEnvironment.AcceptVariable).Append(" is ignored because ").Append(SnapshotEnvironment.CiVariable)
				.Append(" is set: snapshots are never accepted in CI.\n");
		}

		return message.ToString().TrimEnd('\n');
	}

	/// <summary>The differences of <paramref name="comparison"/>, then its line diff if it has one, within the caps.</summary>
	internal static void AppendComparison(StringBuilder message, SnapshotComparison comparison)
	{
		if (comparison.Summary is { Length: > 0 } summary)
			message.Append(summary).Append('\n');
		message.Append(comparison.Differences.Count.ToString(CultureInfo.InvariantCulture)).Append(" difference(s):\n");
		var used = AppendBlocks(message, comparison.Differences, SnapshotMessageLimits.MaxLines, SnapshotMessageLimits.MaxDifferences, "more");
		if (comparison.LineDiff is not { Count: > 0 } hunks)
			return;
		var left = SnapshotMessageLimits.MaxLines - used - 1;
		if (left <= 0)
		{
			message.Append("(line diff left out, see received)\n");
			return;
		}

		message.Append("Line diff:\n");
		AppendBlocks(message, hunks, left, int.MaxValue, "more hunk(s)");
	}

	/// <summary>Lists differences without a line diff. See <see cref="AppendBlocks"/>.</summary>
	internal static void AppendDifferences(StringBuilder message, IReadOnlyList<IReadOnlyList<string>> differences) =>
		AppendComparison(message, new SnapshotComparison(false, differences));

	/// <summary>
	/// Appends blocks up to <paramref name="maxBlocks"/> and <paramref name="maxLines"/>. A block that does not fit in
	/// the remaining lines is left out whole, except a first block that is longer than the cap on its own: that one is
	/// cut, so the message still shows something. Either way it counts in "(+N more, see received)". Returns the lines
	/// used, not counting that note.
	/// </summary>
	private static int AppendBlocks(StringBuilder message, IReadOnlyList<IReadOnlyList<string>> blocks, int maxLines, int maxBlocks, string more)
	{
		var lines = 0;
		var shown = 0;
		foreach (var block in blocks)
		{
			if (shown == maxBlocks)
				break;
			if (lines + block.Count > maxLines)
			{
				if (shown == 0)
				{
					foreach (var line in block.Take(maxLines))
						message.Append(line).Append('\n');
					lines = maxLines;
				}

				break;
			}

			foreach (var line in block)
				message.Append(line).Append('\n');
			lines += block.Count;
			shown++;
		}

		var rest = blocks.Count - shown;
		if (rest > 0)
			message.Append("(+").Append(rest.ToString(CultureInfo.InvariantCulture)).Append(' ').Append(more).Append(", see received)\n");
		return lines;
	}
}

/// <summary>An inline snapshot's place in the source, for its failure message.</summary>
/// <param name="Location">Repository-relative file and line: <c>tests/Acme.Tests/Rendering/X.cs:42</c>.</param>
/// <param name="Method">The assertion method, e.g. <c>MatchInlineSnapshot</c>.</param>
/// <param name="Note">Why <c>SNAPSHOT_ACCEPT=1</c> could not rewrite the literal, if it tried and could not.</param>
internal sealed record InlineFailure(string Location, string Method, string? Note);

/// <summary>Renders comparisons the way failure messages do, for assertions that compare inline values.</summary>
public static class SnapshotMessages
{
	/// <summary>
	/// <c>N difference(s):</c> followed by the differences and the line diff, within
	/// <see cref="SnapshotMessageLimits"/>. Ends without a newline.
	/// </summary>
	public static string Describe(SnapshotComparison comparison)
	{
		ArgumentNullException.ThrowIfNull(comparison);
		var message = new StringBuilder();
		SnapshotFailure.AppendComparison(message, comparison);
		return message.ToString().TrimEnd('\n');
	}
}
