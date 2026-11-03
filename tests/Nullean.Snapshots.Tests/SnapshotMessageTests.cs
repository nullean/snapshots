using System.Text;
using AwesomeAssertions;
using CsCheck;

namespace Nullean.Snapshots.Tests;

public class SnapshotMessageTests
{
	private static readonly Gen<IReadOnlyList<IReadOnlyList<string>>> Differences =
		Gen.String[Gen.Char["xyz{}\"\\ "], 0, 6].Array[1, 12]
			.Select(lines => (IReadOnlyList<string>)[.. lines.Select(l => "~" + l)])
			.Array[0, 45]
			.Select(d => (IReadOnlyList<IReadOnlyList<string>>)d);

	[Test]
	public void CapsAreRespectedAndTheRestIsCounted() =>
		Differences.Sample(differences =>
		{
			var message = new StringBuilder();
			SnapshotFailure.AppendDifferences(message, differences);
			var lines = message.ToString().TrimEnd('\n').Split('\n');

			var body = lines.Skip(1).Where(l => l.StartsWith('~')).ToList();
			var shown = CountFullyShown(differences, body);
			var more = lines.LastOrDefault(l => l.StartsWith("(+", StringComparison.Ordinal));
			var expectedMore = differences.Count - shown;

			return lines[0] == $"{differences.Count} difference(s):"
				&& body.Count <= SnapshotMessageLimits.MaxLines
				&& shown <= SnapshotMessageLimits.MaxDifferences
				&& (expectedMore == 0 ? more is null : more == $"(+{expectedMore} more, see received)");
		});

	// Regression: a difference cut off by the 80-line cap was counted as shown, so "+N more" was one short.
	[Test]
	public void DifferenceThatDoesNotFitIsLeftOutAndCounted()
	{
		var lines = Render(Block(70), Block(20));
		lines.Should().HaveCount(1 + 70 + 1);
		lines[^1].Should().Be("(+1 more, see received)");
	}

	[Test]
	public void OversizedFirstDifferenceIsCutAndCounted()
	{
		var lines = Render(Block(200), Block(1));
		lines.Should().HaveCount(1 + SnapshotMessageLimits.MaxLines + 1);
		lines[^1].Should().Be("(+2 more, see received)");
	}

	[Test]
	public void AtMostTwentyDifferencesAreListed()
	{
		var lines = Render([.. Enumerable.Range(0, 30).Select(_ => Block(1))]);
		lines.Should().HaveCount(1 + SnapshotMessageLimits.MaxDifferences + 1);
		lines[^1].Should().Be("(+10 more, see received)");
	}

	[Test]
	public void ExactlyFullIsNotCut()
	{
		var lines = Render(Block(40), Block(40));
		lines.Should().HaveCount(1 + 80);
		lines.Should().NotContain(l => l.StartsWith("(+", StringComparison.Ordinal));
	}

	private static IReadOnlyList<string> Block(int lines) => [.. Enumerable.Range(0, lines).Select(i => $"line {i}")];

	private static string[] Render(params IReadOnlyList<string>[] differences)
	{
		var message = new StringBuilder();
		SnapshotFailure.AppendDifferences(message, differences);
		return message.ToString().TrimEnd('\n').Split('\n');
	}

	[Test]
	public void RendersPlainTextWithBracesIntact()
	{
		var rendered = Failure(new SnapshotComparison(false, [["@@ line 1 @@", "-{0}", "+{1} {{x}}"]])).Render();
		rendered.Should().Contain("-{0}").And.Contain("+{1} {{x}}").And.NotContain("\u001b");
		rendered.Should().NotContain("\r");
	}

	[Test]
	public void MessageInvariantsHoldForRandomContent() =>
		Differences.Sample(differences =>
		{
			var rendered = Failure(new SnapshotComparison(false, differences)).Render();
			var lines = rendered.Split('\n');
			return !rendered.Contains('\u001b', StringComparison.Ordinal)
				&& !rendered.Contains('\r', StringComparison.Ordinal)
				&& lines.Count(l => l.StartsWith("Accept:   snap accept Acme.Tests/Rendering/HtmlTests/RendersTable", StringComparison.Ordinal)) == 1
				&& lines.Length <= SnapshotMessageLimits.MaxLines + 7;
		});

	[Test]
	public void CiNoteOnlyWhenAcceptWasIgnored()
	{
		Failure(null).Render().Should().NotContain("ignored");
		(Failure(null) with { AcceptIgnoredInCi = true }).Render().Should()
			.EndWith("SNAPSHOT_ACCEPT is ignored because CI is set: snapshots are never accepted in CI.");
	}

	[Test]
	public void TreeNodeFilterLineOnlyWithAFilter()
	{
		Failure(null).Render().Should().Contain("--treenode-filter \"/Acme.Tests/Acme.Tests.Rendering/HtmlTests/RendersTable\"");
		(Failure(null) with { TreeNodeFilter = null }).Render().Should().NotContain("treenode");
	}

	private static int CountFullyShown(IReadOnlyList<IReadOnlyList<string>> differences, List<string> body)
	{
		var consumed = 0;
		var shown = 0;
		foreach (var difference in differences)
		{
			if (consumed + difference.Count > body.Count)
				break;
			consumed += difference.Count;
			shown++;
		}

		return shown;
	}

	internal static SnapshotFailure Failure(SnapshotComparison? comparison) => new(
		"Text",
		"tests/Acme.Tests/.snapshots/Rendering/HtmlTests/RendersTable.verified.txt",
		".artifacts/.snapshots/received/Acme.Tests/Rendering/HtmlTests/RendersTable.received.txt",
		"Acme.Tests/Rendering/HtmlTests/RendersTable",
		comparison,
		null,
		"tests/Acme.Tests",
		"/Acme.Tests/Acme.Tests.Rendering/HtmlTests/RendersTable",
		false);
}
