using DiffPlex;
using DiffPlex.Chunkers;

namespace Nullean.Snapshots;

/// <summary>A line diff rendered as unified hunks: <c>@@ line N @@</c> followed by context, removed and added lines.</summary>
public static class LineDiff
{
	/// <summary>The number of unchanged lines shown around each change.</summary>
	public const int DefaultContext = 3;

	/// <summary>Lines longer than this are cut and end in an ellipsis, so one minified line cannot flood a message.</summary>
	public const int MaxLineLength = 500;

	// Our own splitter rather than DiffPlex's LineChunker: normalised text ends in one newline, and splitting on it
	// leaves an empty last piece that DiffPlex would count as a line. Empty text has no lines at all.
	private static readonly CustomFunctionChunker Chunker = new(SplitLines);

	/// <summary>
	/// Diffs two texts line by line and returns one hunk per group of nearby changes. Each hunk is its header line
	/// followed by lines prefixed with a space (context), <c>-</c> (only in <paramref name="expected"/>) or <c>+</c>
	/// (only in <paramref name="actual"/>). The line number in the header is the hunk's first line in
	/// <paramref name="expected"/>, or in <paramref name="actual"/> when expected is empty.
	/// </summary>
	public static IReadOnlyList<IReadOnlyList<string>> Hunks(string expected, string actual, int context = DefaultContext)
	{
		ArgumentNullException.ThrowIfNull(expected);
		ArgumentNullException.ThrowIfNull(actual);
		ArgumentOutOfRangeException.ThrowIfNegative(context);

		var result = Differ.Instance.CreateDiffs(expected, actual, ignoreWhiteSpace: false, ignoreCase: false, Chunker);
		var oldLines = result.PiecesOld;
		var newLines = result.PiecesNew;
		var blocks = result.DiffBlocks;
		var hunks = new List<IReadOnlyList<string>>();

		var i = 0;
		while (i < blocks.Count)
		{
			// Merge following blocks while their leading context would overlap this hunk's trailing context.
			var last = i;
			while (last + 1 < blocks.Count
				&& blocks[last + 1].DeleteStartA - (blocks[last].DeleteStartA + blocks[last].DeleteCountA) <= 2 * context)
				last++;

			var oldStart = Math.Max(0, blocks[i].DeleteStartA - context);
			var newStart = blocks[i].InsertStartB - (blocks[i].DeleteStartA - oldStart);
			var lines = new List<string>
			{
				$"@@ line {(oldLines.Count == 0 ? newStart : oldStart) + 1} @@"
			};

			var oldPos = oldStart;
			for (var b = i; b <= last; b++)
			{
				var block = blocks[b];
				for (; oldPos < block.DeleteStartA; oldPos++)
					lines.Add(" " + Cap(oldLines[oldPos]));
				for (var d = 0; d < block.DeleteCountA; d++)
					lines.Add("-" + Cap(oldLines[block.DeleteStartA + d]));
				for (var n = 0; n < block.InsertCountB; n++)
					lines.Add("+" + Cap(newLines[block.InsertStartB + n]));
				oldPos = block.DeleteStartA + block.DeleteCountA;
			}

			var oldEnd = Math.Min(oldLines.Count, oldPos + context);
			for (; oldPos < oldEnd; oldPos++)
				lines.Add(" " + Cap(oldLines[oldPos]));

			hunks.Add(lines);
			i = last + 1;
		}

		return hunks;
	}

	internal static string[] SplitLines(string text)
	{
		if (text.Length == 0)
			return [];
		var end = text.EndsWith('\n') ? text.Length - 1 : text.Length;
		return text[..end].Split('\n');
	}

	private static string Cap(string line) =>
		line.Length <= MaxLineLength ? line : string.Concat(line.AsSpan(0, MaxLineLength), "…");
}
