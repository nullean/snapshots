using Nullean.Snapshots.Differences;
using Nullean.Snapshots.Trees;

namespace Nullean.Snapshots.Browser;

/// <summary>
/// What rules can say about a journey's transcript. A target is <c>url</c> or <c>text</c> (a read's page URL or text), or
/// an accessibility selector: <c>role</c>, then optionally a <c>"name"</c> or <c>/regex/</c> and <c>[attr]</c> or
/// <c>[attr=value]</c> filters, with several of them separated by spaces meaning "inside", like CSS descendants:
/// <c>navigation link "Docs" [level=2]</c>.
/// </summary>
/// <remarks>
/// <c>added</c> and <c>removed</c> name the node that appeared or went, or (with a <c>[attr]</c> filter) an attribute of
/// it; <c>changed</c> names the node whose name, value or attribute changed, with the old and new values after it;
/// <c>ignore</c> covers the node, everything inside it, and any change to it.
/// </remarks>
public sealed class JourneyRuleTargets : RuleTargets
{
	/// <summary>The shared instance.</summary>
	public static JourneyRuleTargets Instance { get; } = new();

	/// <inheritdoc />
	public override string HeadingKey => "step";

	/// <inheritdoc />
	public override IRuleTarget ParseTarget(RuleVerb verb, IReadOnlyList<RuleToken> target)
	{
		ArgumentNullException.ThrowIfNull(target);
		if (target is [{ Kind: RuleTokenKind.Word, Value: "url" or "text" } field])
			return new FieldTarget(field.Value);
		// `text "a line"`: a line of a read's text that was added or removed. Also still `text "name"`, an accessibility node of that role.
		if (target is [{ Kind: RuleTokenKind.Word, Value: "text" }, { Kind: not RuleTokenKind.Word } line])
			return new TextLineTarget(ValueOf(line), new AriaTarget([new Term(new RuleValue(target[0]), ValueOf(line), [])]));
		var terms = new List<Term>();
		foreach (var token in target)
		{
			switch (token)
			{
				case { Kind: RuleTokenKind.Word } when token.Value.StartsWith('['):
					if (terms.Count == 0)
						throw new FormatException($"{token.Raw} filters a role; write the role first.");
					terms[^1] = terms[^1] with { Attributes = [.. terms[^1].Attributes, ParseAttribute(token)] };
					break;
				case { Kind: RuleTokenKind.Word }:
					terms.Add(new Term(new RuleValue(token), null, []));
					break;
				default:
					if (terms.Count == 0 || terms[^1].Name is not null)
						throw new FormatException($"{token.Raw} is a name; it follows a role, once.");
					terms[^1] = terms[^1] with { Name = ValueOf(token) };
					break;
			}
		}

		if (terms.Count == 0)
			throw new FormatException("name what the rule is about: url, text, or an accessibility role with an optional \"name\".");
		return new AriaTarget(terms);
	}

	private static RuleValue ValueOf(RuleToken token)
	{
		try
		{
			return new RuleValue(token);
		}
		catch (ArgumentException e)
		{
			throw new FormatException($"{token.Raw} is not a regular expression: {e.Message}");
		}
	}

	private static (string Name, RuleValue? Value) ParseAttribute(RuleToken token)
	{
		if (!token.Value.EndsWith(']') || token.Value.Length < 3)
			throw new FormatException($"{token.Raw} is not [attribute] or [attribute=value].");
		var inner = token.Value[1..^1];
		var equals = inner.IndexOf('=', StringComparison.Ordinal);
		return equals < 0 ? (inner, null) : (inner[..equals], new RuleValue(new RuleToken(token.Raw, RuleTokenKind.Word, inner[(equals + 1)..])));
	}

	/// <inheritdoc />
	public override IReadOnlyList<string> Suggest(DifferenceFacts facts)
	{
		ArgumentNullException.ThrowIfNull(facts);
		if (facts.Path is [{ Kind: PathSegmentKind.Key, Key: "url" or "text" } field])
			return [field.Key];
		if (Locate(facts) is not { } found)
			return ["text"];
		if (found.Candidates is not null)
			return [.. found.Candidates.Select(Describe)];
		if (found.Inside)
		{
			var role = Field(found.Ancestors[^1], "role") ?? "*";
			return facts.Kind is DifferenceKind.Missing or DifferenceKind.Unexpected && found.FieldKey is { } key ? [$"{role} [{key}]"] : [role];
		}

		return [Describe(found.Ancestors[^1])];
	}

	private static string Describe(MapNode node) => Field(node, "role") + (Field(node, "name") is { } name ? " " + Quote(name) : string.Empty);

	internal static string? Field(MapNode node, string key) => (node.Entries.FirstOrDefault(e => e.Key.Canonical == key).Value as ScalarNode)?.Canonical;

	/// <summary>Where a difference sits in an accessibility tree.</summary>
	/// <param name="Ancestors">The aria nodes from the root down to the one the difference is at or in.</param>
	/// <param name="Candidates">When the difference is a whole list of nodes (a tree or a node's children), those nodes.</param>
	/// <param name="Inside">The difference is in a node's name, value or attributes, not the node itself.</param>
	/// <param name="FieldKey">The last key of the path when it is inside a node.</param>
	internal sealed record Located(IReadOnlyList<MapNode> Ancestors, IReadOnlyList<MapNode>? Candidates, bool Inside, string? FieldKey);

	internal static Located? Locate(DifferenceFacts facts)
	{
		var path = facts.Path;
		if (path.Count == 0 || path[0] is not { Kind: PathSegmentKind.Key, Key: "tree" })
			return null;
		var ancestors = new List<MapNode>();
		var lastNode = -1;
		for (var i = 1; i < path.Count; i++)
		{
			if (
				path[i].Kind == PathSegmentKind.Index && path[i - 1] is { Kind: PathSegmentKind.Key, Key: "tree" or "children" } && i + 1 < facts.Chain.Count &&
				facts.Chain[i + 1] is MapNode node
			)
			{
				ancestors.Add(node);
				lastNode = i;
			}
		}

		var tail = path.Count - 1 - lastNode;
		if (lastNode < 0 || tail == 0)
		{
			// The whole tree, or a node's whole list of children, appeared or went.
			if (facts.Node is SeqNode list && path[^1] is { Kind: PathSegmentKind.Key, Key: "tree" or "children" })
				return new Located(ancestors, [.. list.Items.OfType<MapNode>()], false, null);
			return ancestors.Count == 0 ? null : new Located(ancestors, null, false, null);
		}

		if (tail == 1 && path[^1] is { Kind: PathSegmentKind.Key, Key: "children" } && facts.Node is SeqNode children)
			return new Located(ancestors, [.. children.Items.OfType<MapNode>()], false, null);
		return new Located(ancestors, null, true, facts.LastKey);
	}

	private sealed record Term(RuleValue Role, RuleValue? Name, IReadOnlyList<(string Name, RuleValue? Value)> Attributes);

	/// <inheritdoc />
	public override bool IsText(DifferenceFacts facts)
	{
		ArgumentNullException.ThrowIfNull(facts);
		return facts.Path is [{ Kind: PathSegmentKind.Key, Key: "text" }];
	}

	private sealed class TextLineTarget(RuleValue line, IRuleTarget aria) : IRuleTarget
	{
		public bool Matches(DifferenceFacts facts, RuleVerb verb) => aria.Matches(facts, verb);

		public bool MatchesLine(DifferenceFacts facts, RuleVerb verb, string text) =>
			facts.Path is [{ Kind: PathSegmentKind.Key, Key: "text" }] && line.Matches(text);
	}

	private sealed class FieldTarget(string field) : IRuleTarget
	{
		public bool Matches(DifferenceFacts facts, RuleVerb verb) => facts.Path is [{ Kind: PathSegmentKind.Key } only] && only.Key == field;
	}

	private sealed class AriaTarget(IReadOnlyList<Term> terms) : IRuleTarget
	{
		public bool Matches(DifferenceFacts facts, RuleVerb verb)
		{
			if (Locate(facts) is not { } found)
				return false;
			if (found.Candidates is not null)
				return found.Candidates.Any(c => MatchesChain([.. found.Ancestors, c], verb, false, null, facts.Kind, null));
			// A renamed node is the one the rule names whether it is found by its old or its new name.
			var renamedTo = found.Inside && found.FieldKey == "name" && facts.Kind is DifferenceKind.Changed ? facts.NewText : null;
			return MatchesChain(found.Ancestors, verb, found.Inside, found.FieldKey, facts.Kind, renamedTo);
		}

		private bool MatchesChain(IReadOnlyList<MapNode> nodes, RuleVerb verb, bool inside, string? fieldKey, DifferenceKind kind, string? renamedTo)
		{
			// An attribute that is on one side only cannot be checked against the node; the rule must name it.
			var oneSided = inside && kind is DifferenceKind.Missing or DifferenceKind.Unexpected;
			if (oneSided && verb != RuleVerb.Ignore && !terms[^1].Attributes.Any(a => a.Name == fieldKey))
				return false;
			var ends = verb == RuleVerb.Ignore ? Enumerable.Range(0, nodes.Count) : [nodes.Count - 1];
			foreach (var end in ends)
			{
				var isDifferenceNode = end == nodes.Count - 1;
				if (!TermMatches(terms[^1], nodes[end], isDifferenceNode && oneSided ? fieldKey : null, isDifferenceNode ? renamedTo : null))
					continue;
				// The earlier terms are ancestors, in order, anywhere above.
				var at = end - 1;
				var matched = true;
				for (var t = terms.Count - 2; t >= 0 && matched; t--)
				{
					while (at >= 0 && !TermMatches(terms[t], nodes[at], null, null))
						at--;
					matched = at >= 0;
					at--;
				}

				if (matched)
					return true;
			}

			return false;
		}

		private static bool TermMatches(Term term, MapNode node, string? skipAttribute, string? alternateName)
		{
			if (!term.Role.Matches(Field(node, "role")))
				return false;
			if (term.Name is not null && !term.Name.Matches(Field(node, "name")) && !term.Name.Matches(alternateName))
				return false;
			var attributes = node.Entries.FirstOrDefault(e => e.Key.Canonical == "attrs").Value as MapNode;
			foreach (var (name, value) in term.Attributes)
			{
				if (name == skipAttribute)
					continue;
				var entry = attributes?.Entries.FirstOrDefault(e => e.Key.Canonical == name);
				if (entry is not { Value: ScalarNode scalar })
					return false;
				if (value is not null && !value.Matches(scalar.Canonical))
					return false;
			}

			return true;
		}
	}
}
