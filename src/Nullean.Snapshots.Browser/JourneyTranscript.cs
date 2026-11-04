using System.Text;
using Nullean.Cdp.Journeys;
using Nullean.Snapshots.Trees;

namespace Nullean.Snapshots.Browser;

/// <summary>
/// A journey's transcript: one section per checkpoint, the snapshot a journey is compared by.
/// </summary>
/// <remarks>
/// <code>
/// ## get text #log
/// url: {origin}/shop.html
/// > clicked hello
///
/// ## snapshot -c -s main
/// url: {origin}/shop.html
/// - main
///   - heading "Shop" [level=1]
/// </code>
/// A section is headed by the step's command, normalised and without its line number, so inserting a step shifts
/// nothing. A <c>snapshot</c> prints its accessibility tree; every other read prints its text with each line quoted by
/// <c>&gt; </c>, so page text can never start a section or pass for a tree. The base URL's root is scrubbed to
/// <c>{origin}</c>.
/// </remarks>
/// <param name="File">The journey file.</param>
/// <param name="Text">The transcript.</param>
/// <param name="Failure">The step the journey stopped at, if it did not finish.</param>
public sealed record JourneyTranscript(string File, string Text, JourneyFailure? Failure)
{
	/// <summary>What the base URL's root is replaced with.</summary>
	public const string Origin = "{origin}";

	/// <summary>Renders the checkpoints of <paramref name="result"/> as a transcript.</summary>
	public static string Render(JourneyResult result, Uri? baseUrl)
	{
		ArgumentNullException.ThrowIfNull(result);
		return Render(result.Checkpoints, baseUrl);
	}

	/// <summary>Renders <paramref name="checkpoints"/> as a transcript.</summary>
	public static string Render(IReadOnlyList<Checkpoint> checkpoints, Uri? baseUrl)
	{
		ArgumentNullException.ThrowIfNull(checkpoints);
		var root = Root(baseUrl);
		string Scrub(string text) => root is null ? text : text.Replace(root, Origin, StringComparison.Ordinal);

		var text = new StringBuilder();
		foreach (var checkpoint in checkpoints)
		{
			if (text.Length > 0)
				text.Append('\n');
			text.Append("## ").Append(checkpoint.Command).Append('\n');
			text.Append("url: ").Append(Scrub(checkpoint.Url)).Append('\n');
			var output = Scrub(checkpoint.Output.Replace("\r\n", "\n", StringComparison.Ordinal));
			if (IsSnapshot(checkpoint.Command))
			{
				if (output.Length > 0)
					text.Append(output.TrimEnd('\n')).Append('\n');
			}
			else
			{
				foreach (var line in output.Split('\n'))
					text.Append(line.Length == 0 ? ">" : "> " + line).Append('\n');
			}
		}

		return text.ToString();
	}

	private static bool IsSnapshot(string command) => command == "snapshot" || command.StartsWith("snapshot ", StringComparison.Ordinal);

	/// <summary>
	/// The part of a base URL that differs between machines and runs: the origin of an http(s) URL, or the whole
	/// folder of a <c>file:</c> URL, without a trailing slash.
	/// </summary>
	internal static string? Root(Uri? baseUrl) => baseUrl switch
	{
		null => null,
		{ IsFile: true } => baseUrl.AbsoluteUri.TrimEnd('/'),
		_ => baseUrl.GetLeftPart(UriPartial.Authority),
	};

	/// <summary>Parses a transcript into a sequence of sections: <c>{step, url, tree}</c> or <c>{step, url, text}</c>.</summary>
	/// <exception cref="FormatException">The text is not a transcript.</exception>
	public static SeqNode Parse(string text)
	{
		ArgumentNullException.ThrowIfNull(text);
		var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
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
				throw new FormatException($"Line {number}: a section starts with '## ' and the step's command.");
			var step = ScalarNode.String(lines[i][3..], new SourceSpan(number, 4));
			i++;
			if (i >= lines.Length || !lines[i].StartsWith("url: ", StringComparison.Ordinal))
				throw new FormatException($"Line {i + 1}: the section's second line is 'url: ' and the page's URL.");
			var url = ScalarNode.String(lines[i][5..], new SourceSpan(i + 1, 6));
			i++;

			var start = i;
			while (i < lines.Length && lines[i].Length > 0 && !lines[i].StartsWith("## ", StringComparison.Ordinal))
				i++;
			var body = lines[start..i];
			var entries = new List<KeyValuePair<ScalarNode, Node>> { new(ScalarNode.String("step"), step), new(ScalarNode.String("url"), url) };
			if (body.Length > 0 && body[0].StartsWith('>'))
			{
				var quoted = new List<string>();
				for (var j = 0; j < body.Length; j++)
				{
					quoted.Add(body[j] switch
					{
						">" => string.Empty,
						var l when l.StartsWith("> ", StringComparison.Ordinal) => l[2..],
						_ => throw new FormatException($"Line {start + j + 1}: every line of a read's text starts with '> '."),
					});
				}

				entries.Add(new(ScalarNode.String("text"), ScalarNode.String(string.Join('\n', quoted), new SourceSpan(start + 1, 1))));
			}
			else
				entries.Add(new(ScalarNode.String("tree"), AriaTree.Parse(string.Join('\n', body), start + 1)));

			sections.Add(new MapNode(entries, new SourceSpan(number, 1)));
		}

		return new SeqNode(sections);
	}
}

/// <summary>Journey transcripts, compared section by section as trees, with the line diff after.</summary>
public sealed class JourneySnapshotFormat(TreeDiffOptions? options = null) : TreeSnapshotFormat(options)
{
	/// <inheritdoc />
	public override string Name => "Journey";

	/// <inheritdoc />
	public override string Extension => "journey";

	/// <inheritdoc />
	public override Node Parse(string text) => JourneyTranscript.Parse(text);

	/// <inheritdoc />
	public override SnapshotComparison Compare(string verified, string actual) => WithLineDiff(base.Compare(verified, actual), verified, actual);

	internal static SnapshotComparison WithLineDiff(SnapshotComparison comparison, string verified, string actual) =>
		comparison.IsMatch ? comparison : comparison with { LineDiff = LineDiff.Hunks(verified, actual) };
}

/// <summary>Accessibility trees of one page, compared as trees, with the line diff after.</summary>
public sealed class AriaSnapshotFormat(TreeDiffOptions? options = null) : TreeSnapshotFormat(options)
{
	/// <inheritdoc />
	public override string Name => "Accessibility tree";

	/// <inheritdoc />
	public override string Extension => "aria";

	/// <inheritdoc />
	public override Node Parse(string text) => AriaTree.Parse(text);

	/// <inheritdoc />
	public override SnapshotComparison Compare(string verified, string actual) =>
		JourneySnapshotFormat.WithLineDiff(base.Compare(verified, actual), verified, actual);
}
