using AwesomeAssertions;
using CsCheck;

namespace Nullean.Snapshots.Tests;

public class LineDiffTests
{
	/// <summary>Short lines from a small alphabet, so random texts share lines and the differ has work to do.</summary>
	private static readonly Gen<string> Text = Gen
		.String[Gen.Char["ab{} "], 0, 3]
		.Array[0, 25]
		.Select(lines => SnapshotText.Normalize(string.Join("\n", lines)));

	[Test]
	public void IdenticalTextsHaveNoHunks() => Text.Sample(t => LineDiff.Hunks(t, t).Count == 0);

	[Test]
	public void ApplyingTheHunksToExpectedGivesActual() =>
		Gen.Select(Text, Text).Sample((expected, actual) =>
		{
			var patched = Apply(expected, LineDiff.Hunks(expected, actual));
			return patched.SequenceEqual(LineDiff.SplitLines(actual));
		});

	[Test]
	public void HunksAreEmptyExactlyWhenTextsAreEqual() =>
		Gen.Select(Text, Text).Sample((expected, actual) => LineDiff.Hunks(expected, actual).Count == 0 == (expected == actual));

	// Regression: DiffPlex's own line chunker counted the empty piece after the trailing newline as a line, so an
	// appended line showed up as a changed last line.
	private static readonly string[][] AppendedHunks = [["@@ line 1 @@", " a", "+b"]];
	private static readonly string[][] ChangedHunks = [["@@ line 2 @@", " 2", " 3", " 4", "-5", "+FIVE", " 6", " 7", " 8"]];
	private static readonly string[][] AddedToEmptyHunks = [["@@ line 1 @@", "+x", "+y"]];

	[Test]
	public void AppendedLineIsOnlyAnAddition() => LineDiff.Hunks("a\n", "a\nb\n").Should().BeEquivalentTo(AppendedHunks, o => o.WithStrictOrdering());

	[Test]
	public void ChangeIsShownWithThreeLinesOfContext() =>
		LineDiff.Hunks("1\n2\n3\n4\n5\n6\n7\n8\n9\n", "1\n2\n3\n4\nFIVE\n6\n7\n8\n9\n").Should().BeEquivalentTo(ChangedHunks, o => o.WithStrictOrdering());

	[Test]
	public void FarApartChangesAreSeparateHunks()
	{
		var expected = string.Join("\n", Enumerable.Range(1, 30)) + "\n";
		var actual = expected.Replace("\n2\n", "\nTWO\n", StringComparison.Ordinal).Replace("\n28\n", "\nTWENTY-EIGHT\n", StringComparison.Ordinal);
		var hunks = LineDiff.Hunks(expected, actual);
		hunks.Select(h => h[0]).Should().Equal("@@ line 1 @@", "@@ line 25 @@");
	}

	[Test]
	public void EverythingAddedToEmptyText() => LineDiff.Hunks("", "x\ny\n").Should().BeEquivalentTo(AddedToEmptyHunks, o => o.WithStrictOrdering());

	[Test]
	public void LongLinesAreCut()
	{
		var hunk = LineDiff.Hunks("a\n", new string('x', 2000) + "\n").Single();
		hunk.Should().Contain(l => l.StartsWith('+') && l.Length == LineDiff.MaxLineLength + 2 && l.EndsWith('…'));
	}

	/// <summary>Replays hunks onto the expected lines; throws when a context or removed line does not match.</summary>
	private static List<string> Apply(string expected, IReadOnlyList<IReadOnlyList<string>> hunks)
	{
		var lines = LineDiff.SplitLines(expected);
		var result = new List<string>();
		var pos = 0;
		foreach (var hunk in hunks)
		{
			var start = lines.Length == 0 ? 0 : int.Parse(hunk[0].Split(' ')[2], System.Globalization.CultureInfo.InvariantCulture) - 1;
			while (pos < start)
				result.Add(lines[pos++]);
			foreach (var line in hunk.Skip(1))
			{
				switch (line[0])
				{
					case ' ':
						if (lines[pos] != line[1..])
							throw new InvalidOperationException($"context mismatch at {pos}");
						result.Add(lines[pos++]);
						break;
					case '-':
						if (lines[pos] != line[1..])
							throw new InvalidOperationException($"removal mismatch at {pos}");
						pos++;
						break;
					case '+':
						result.Add(line[1..]);
						break;
				}
			}
		}

		while (pos < lines.Length)
			result.Add(lines[pos++]);
		return result;
	}
}
