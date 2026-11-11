using System.Text;
using Nullean.Snapshots.Differences;
using Nullean.Snapshots.Trees;

namespace Nullean.Snapshots.Http;

/// <summary>
/// What rules can say about a <c>.http</c> transcript. A target is <c>status</c>, <c>header NAME</c> (a glob, case
/// insensitive), <c>body [PATH]</c> (the path inside a JSON body, <c>$.hits[*].score</c>, with <c>[*]</c>, <c>.*</c> and
/// <c>..key</c>), <c>text</c> (a text body), <c>binary</c> or <c>request</c> (the heading).
/// </summary>
/// <remarks>An <c>ignore body $.meta</c> covers <c>$.meta</c> and everything inside it.</remarks>
public sealed class HttpRuleTargets : RuleTargets
{
	/// <summary>The shared instance.</summary>
	public static HttpRuleTargets Instance { get; } = new();

	/// <inheritdoc />
	public override string HeadingKey => "request";

	/// <inheritdoc />
	public override IRuleTarget ParseTarget(RuleVerb verb, IReadOnlyList<RuleToken> target)
	{
		ArgumentNullException.ThrowIfNull(target);
		var first = target.Count > 0 && target[0].Kind == RuleTokenKind.Word ? target[0].Value : string.Empty;
		switch (first)
		{
			case "status" or "text" or "binary" or "request" when target.Count == 1:
				return new KeyTarget(first);
			case "header" when target.Count == 2 && target[1].Kind != RuleTokenKind.Regex:
				return new HeaderTarget(target[1].Value.ToLowerInvariant());
			case "body" when target.Count <= 2 && (target.Count == 1 || target[1].Kind != RuleTokenKind.Regex):
				try
				{
					return new BodyTarget(NodePath.Parse(target.Count == 1 ? "$" : target[1].Value));
				}
				catch (FormatException e)
				{
					throw new FormatException(e.Message, e);
				}
			default:
				throw new FormatException(
					$"'{string.Join(' ', target.Select(t => t.Raw))}' is not a target: status, header NAME, body [PATH], text, binary or request."
				);
		}
	}

	/// <inheritdoc />
	public override IReadOnlyList<string> Suggest(DifferenceFacts facts)
	{
		ArgumentNullException.ThrowIfNull(facts);
		var key = facts.Path[0].Key;
		switch (key)
		{
			case "status" or "binary" or "request":
				return [key];
			case "text" or "text-type":
				return ["text"];
			case "headers" when facts.Path.Count >= 2:
				return [$"header {facts.Path[1].Key}"];
			case "headers":
				return facts.Node is MapNode { Entries.Count: > 0 } headers ? [.. headers.Entries.Select(e => "header " + e.Key.Canonical)] : ["header *"];
			case "body":
				// An index in a suggestion is any index: the next run's array will not be the same length.
				var path = new StringBuilder("$");
				foreach (var segment in facts.Path.Skip(1))
					path.Append(segment.Kind == PathSegmentKind.Index ? "[*]" : NodePath.Format([segment])[1..]);
				var text = path.ToString();
				// A key with a space or a quote cannot be one bare word.
				return [text.Length == 1 ? "body" : text.Any(c => char.IsWhiteSpace(c) || c == '"') ? $"body {Quote(text)}" : $"body {text}"];
			default:
				return ["request"];
		}
	}

	private sealed class KeyTarget(string key) : IRuleTarget
	{
		public bool Matches(DifferenceFacts facts, RuleVerb verb)
		{
			var actual = facts.Path[0].Key;
			if (key == "text")
				return actual is "text" or "text-type" && (facts.Path.Count == 1 || verb == RuleVerb.Ignore);
			return actual == key && (facts.Path.Count == 1 || verb == RuleVerb.Ignore);
		}
	}

	private sealed class HeaderTarget(string pattern) : IRuleTarget
	{
		public bool Matches(DifferenceFacts facts, RuleVerb verb)
		{
			if (facts.Path[0].Key != "headers")
				return false;
			if (facts.Path.Count >= 2)
				return facts.Path[1].Key is { } name && Glob.IsMatch(pattern, name.ToLowerInvariant());
			// The whole header map appeared, went or changed kind.
			return facts.Node is MapNode map && map.Entries.Any(e => Glob.IsMatch(pattern, e.Key.Canonical.ToLowerInvariant()));
		}
	}

	private sealed class BodyTarget(IReadOnlyList<PathSegment> pattern) : IRuleTarget
	{
		public bool Matches(DifferenceFacts facts, RuleVerb verb)
		{
			if (facts.Path[0].Key != "body")
				return false;
			var rest = facts.Path.Skip(1).ToList();
			if (verb != RuleVerb.Ignore)
				return NodePath.Matches(pattern, rest);
			// Ignore reaches everything below the node the pattern names.
			for (var length = 0; length <= rest.Count; length++)
			{
				if (NodePath.Matches(pattern, [.. rest.Take(length)]))
					return true;
			}

			return false;
		}
	}
}
