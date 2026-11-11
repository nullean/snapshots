using Nullean.Snapshots.Trees;

namespace Nullean.Snapshots.Differences;

/// <summary>
/// One difference, described for rules: where it is (a section, then a path inside it), what was on each side, and the
/// nodes along the way.
/// </summary>
public sealed class DifferenceFacts
{
	internal DifferenceFacts(TreeDifference difference, int sectionIndex, string? heading, IReadOnlyList<PathSegment> path, IReadOnlyList<Node> chain)
	{
		Difference = difference;
		SectionIndex = sectionIndex;
		Heading = heading;
		Path = path;
		Chain = chain;
	}

	/// <summary>The difference.</summary>
	public TreeDifference Difference { get; }

	/// <summary>The index of the section (step or request) it is in; -1 for a difference of the whole transcript.</summary>
	public int SectionIndex { get; }

	/// <summary>The section's heading: the step's command or the request as written.</summary>
	public string? Heading { get; }

	/// <summary>The steps from the section down to the difference; empty when the whole section is added or removed.</summary>
	public IReadOnlyList<PathSegment> Path { get; }

	/// <summary>
	/// The nodes on the way: <c>Chain[0]</c> is the section, <c>Chain[i]</c> the node <c>Path[i - 1]</c> leads to. From
	/// the target's tree for an added node, the baseline's otherwise.
	/// </summary>
	public IReadOnlyList<Node> Chain { get; }

	/// <summary>The node the difference is about.</summary>
	public Node Node => Chain[^1];

	/// <summary>The kind of difference.</summary>
	public DifferenceKind Kind => Difference.Kind;

	/// <summary>The baseline's value when it is a scalar.</summary>
	public string? OldText => (Difference.Expected as ScalarNode)?.Canonical;

	/// <summary>The target's value when it is a scalar.</summary>
	public string? NewText => (Difference.Actual as ScalarNode)?.Canonical;

	/// <summary>The key of the last step of <see cref="Path"/>, if it is a key.</summary>
	public string? LastKey => Path.Count > 0 && Path[^1].Kind == PathSegmentKind.Key ? Path[^1].Key : null;

	internal static DifferenceFacts Create(TreeDifference difference, Node baseline, Node actual, string headingKey)
	{
		// A difference is in the verified tree, except an unexpected one, which is in the actual tree.
		var root = difference.Kind == DifferenceKind.Unexpected ? actual : baseline;
		var segments = NodePath.Parse(difference.Path);
		var chain = new List<Node>();
		var node = root;
		foreach (var segment in segments)
		{
			Node? next = (node, segment.Kind) switch
			{
				(SeqNode seq, PathSegmentKind.Index) when segment.Index < seq.Items.Count => seq.Items[segment.Index],
				(MapNode map, PathSegmentKind.Key) => map.Entries.FirstOrDefault(e => e.Key.Canonical == segment.Key).Value,
				_ => null,
			};
			if (next is null)
				break;
			node = next;
			chain.Add(node);
		}

		if (segments.Count == 0 || segments[0].Kind != PathSegmentKind.Index || chain.Count == 0)
			return new DifferenceFacts(difference, -1, null, segments, [difference.Expected ?? difference.Actual ?? root]);

		var heading = (chain[0] as MapNode)?.Entries.FirstOrDefault(e => e.Key.Canonical == headingKey).Value is ScalarNode h ? h.Canonical : null;
		var path = segments.Skip(1).ToList();
		// A chain shorter than the path means the node is not in the tree it should be; the difference's own node stands in.
		while (chain.Count < path.Count + 1)
			chain.Add(difference.Expected ?? difference.Actual ?? chain[^1]);
		return new DifferenceFacts(difference, segments[0].Index, heading, path, chain);
	}
}

/// <summary>What one rule's target selects.</summary>
public interface IRuleTarget
{
	/// <summary>Whether the target selects the difference. <paramref name="verb"/> lets <see cref="RuleVerb.Ignore"/> reach below the node it names.</summary>
	bool Matches(DifferenceFacts facts, RuleVerb verb);

	/// <summary>
	/// Whether the target selects a single <paramref name="line"/> that was added to (<see cref="RuleVerb.Added"/>) or removed from
	/// (<see cref="RuleVerb.Removed"/>) a multi-line text, for a rule like <c>added text "GET /x 404"</c>. Only called for a text the
	/// format names in <see cref="RuleTargets.IsText"/>. Targets that do not read lines say no.
	/// </summary>
	bool MatchesLine(DifferenceFacts facts, RuleVerb verb, string line) => false;
}

/// <summary>
/// What a transcript format contributes to rules: how to read the words after a verb, and how to write them for a
/// difference. Sections are the transcript's top-level items, each headed by a key.
/// </summary>
public abstract class RuleTargets
{
	/// <summary>The key holding a section's heading: <c>step</c> for journeys, <c>request</c> for <c>.http</c> runs.</summary>
	public abstract string HeadingKey { get; }

	/// <summary>Reads the words after the verb, up to the old and new values.</summary>
	/// <exception cref="FormatException">The words are not a target of this format; the message says why.</exception>
	public abstract IRuleTarget ParseTarget(RuleVerb verb, IReadOnlyList<RuleToken> target);

	/// <summary>
	/// The targets, as written in a rule, that would select <paramref name="facts"/>; one per thing that was added or
	/// removed. Never called for a difference of a whole section, which needs no target.
	/// </summary>
	public abstract IReadOnlyList<string> Suggest(DifferenceFacts facts);

	/// <summary>
	/// Whether <paramref name="facts"/> is a change of a text whose lines rules can name: a read's text, a response's text body. For
	/// such a change a rule can excuse each added and removed line (<c>added text "line"</c>), and a suggestion says so line by line.
	/// </summary>
	public virtual bool IsText(DifferenceFacts facts) => false;

	/// <summary><paramref name="value"/> as a <c>"quoted"</c> word a rule reads back as the same text.</summary>
	protected static string Quote(string value) => RuleFileParser.Quote(value);
}
