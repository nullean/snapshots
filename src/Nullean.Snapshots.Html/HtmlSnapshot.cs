using AngleSharp.Diffing;
using AngleSharp.Diffing.Core;
using AngleSharp.Diffing.Strategies;
using AngleSharp.Dom;
using AngleSharp.Html;
using AngleSharp.Html.Parser;

namespace Nullean.Snapshots.Html;

/// <summary>Pretty-prints and compares HTML.</summary>
public static class HtmlSnapshot
{
	/// <summary>How many lines of a node's HTML a difference shows before it is cut.</summary>
	public const int MaxNodeLines = 6;

	/// <summary>The default format: the body, nothing removed.</summary>
	public static SnapshotFormat Format { get; } = new HtmlSnapshotFormat();

	/// <summary>
	/// Parses <paramref name="html"/>, applies <paramref name="options"/> and pretty-prints the selected element's
	/// content, one node per line with tab indentation. Whitespace-only text between top-level elements is dropped.
	/// </summary>
	/// <exception cref="InvalidOperationException">The selector in <paramref name="options"/> matches nothing.</exception>
	public static string Pretty(string html, HtmlSnapshotOptions? options = null)
	{
		ArgumentNullException.ThrowIfNull(html);
		var document = new HtmlParser().ParseDocument(html);
		IElement? root = options?.Selector is { } selector ? document.QuerySelector(selector) : document.Body;
		if (root is null)
		{
			throw new InvalidOperationException(options?.Selector is { } s ? $"The HTML has no element matching '{s}'." : "The HTML has no body.");
		}

		if (options is not null)
			Apply(root, options);

		var formatter = new WhitespaceKeepingFormatter { NewLine = "\n", Indentation = "\t" };
		using var writer = new StringWriter { NewLine = "\n" };
		// A selected <pre> (or <textarea>) has whitespace that is its content: its children are not tidied at all.
		var preformattedRoot = root.LocalName is "pre" or "textarea" or "listing";
		foreach (var node in root.ChildNodes)
		{
			if (preformattedRoot)
			{
				node.ToHtml(writer, HtmlMarkupFormatter.Instance);
				continue;
			}

			if (node is IText text)
			{
				// Escaped as the formatter would: the raw data of "&lt;a" is "<a", which reparses as a tag.
				if (!string.IsNullOrWhiteSpace(text.Data))
					writer.Write("\n" + HtmlMarkupFormatter.Instance.Text(text).Trim());
				continue;
			}

			node.ToHtml(writer, formatter);
		}

		return SnapshotText.Normalize(writer.ToString().TrimStart('\n'));
	}

	/// <summary>Compares two HTML texts structurally, as stored by <see cref="Pretty"/>.</summary>
	public static SnapshotComparison Compare(string expected, string actual)
	{
		ArgumentNullException.ThrowIfNull(expected);
		ArgumentNullException.ThrowIfNull(actual);
		var diffs = DiffBuilder
			.Compare(expected)
			.WithTest(actual)
			.WithOptions(o => o.AddDefaultOptions().AddComparer(PreformattedText, StrategyType.Specialized))
			.Build()
			.ToList();
		return diffs.Count == 0 ? SnapshotComparison.Match : new SnapshotComparison(false, [.. diffs.Select(Describe)], LineDiff.Hunks(expected, actual));
	}

	/// <summary>
	/// Text inside a <c>pre</c>, <c>textarea</c> or <c>listing</c> is compared exactly. The comparer's own rule covers only text that is a direct
	/// child of one, so the usual <c>&lt;pre&gt;&lt;code&gt;</c> block had its newlines and spaces ignored.
	/// </summary>
	private static CompareResult PreformattedText(in Comparison comparison, CompareResult currentDecision)
	{
		if (
			comparison.Control.Node is not IText control || comparison.Test.Node is not IText test ||
			!(Preformatted.Inside(control) || Preformatted.Inside(test))
		)
			return currentDecision;
		return string.Equals(control.Data, test.Data, StringComparison.Ordinal) ? CompareResult.Same : CompareResult.FromDiff(new NodeDiff(comparison));
	}

	private static void Apply(IElement root, HtmlSnapshotOptions options)
	{
		foreach (var selector in options.RemoveSelectors)
		{
			foreach (var element in root.QuerySelectorAll(selector).ToList())
				element.Remove();
		}

		foreach (var (selector, patterns) in options.RemovedAttributes)
		{
			IEnumerable<IElement> elements = selector is null ? [root, .. root.QuerySelectorAll("*")] : root.QuerySelectorAll(selector);
			foreach (var element in elements)
			{
				foreach (var name in element.Attributes.Select(a => a.Name).ToList())
				{
					if (patterns.Any(p => Matches(p, name)))
						element.RemoveAttribute(name);
				}
			}
		}

		foreach (var transform in options.Transforms)
			transform(root);
	}

	internal static bool Matches(string pattern, string name)
	{
		var parts = pattern.Split('*');
		if (parts.Length == 1)
			return string.Equals(pattern, name, StringComparison.OrdinalIgnoreCase);
		if (!name.StartsWith(parts[0], StringComparison.OrdinalIgnoreCase) || !name.EndsWith(parts[^1], StringComparison.OrdinalIgnoreCase))
			return false;
		var at = parts[0].Length;
		var end = name.Length - parts[^1].Length;
		for (var i = 1; i < parts.Length - 1; i++)
		{
			var found = name.IndexOf(parts[i], at, StringComparison.OrdinalIgnoreCase);
			if (found < 0 || found + parts[i].Length > end)
				return false;
			at = found + parts[i].Length;
		}

		return at <= end;
	}

	/// <summary>
	/// One difference as lines: the label docs-builder's <c>HtmlDiffString</c> used, then what was expected and what
	/// was found.
	/// </summary>
	internal static IReadOnlyList<string> Describe(IDiff diff)
	{
		var label = diff switch
		{
			NodeDiff nd when nd.Target == DiffTarget.Text && nd.Control.Path == nd.Test.Path => $"The text in {nd.Control.Path} is different.",
			NodeDiff nd when nd.Target == DiffTarget.Text =>
				$"The expected {Kind(nd.Control)} at {nd.Control.Path} and the actual {Kind(nd.Test)} at {nd.Test.Path} are different.",
			NodeDiff nd when nd.Control.Path == nd.Test.Path => $"The {Kind(nd.Control)}s at {nd.Control.Path} are different.",
			NodeDiff nd => $"The expected {Kind(nd.Control)} at {nd.Control.Path} and the actual {Kind(nd.Test)} at {nd.Test.Path} are different.",
			AttrDiff ad when ad.Control.Path == ad.Test.Path => $"The values of the attribute at {ad.Control.Path} are different.",
			AttrDiff ad => $"The expected attribute at {ad.Control.Path} and the actual attribute at {ad.Test.Path} are different.",
			MissingNodeDiff md => $"The {Kind(md.Control)} at {md.Control.Path} is missing.",
			MissingAttrDiff ma => $"The attribute at {ma.Control.Path} is missing.",
			UnexpectedNodeDiff ud => $"The {Kind(ud.Test)} at {ud.Test.Path} was not expected.",
			UnexpectedAttrDiff ua => $"The attribute at {ua.Test.Path} was not expected.",
			_ => $"{diff.GetType().Name} ({diff.Target}).",
		};

		var (expected, actual) = diff switch
		{
			NodeDiff nd => (Text(nd.Control.Node), Text(nd.Test.Node)),
			AttrDiff ad => (Text(ad.Control.Attribute), Text(ad.Test.Attribute)),
			MissingNodeDiff md => (Text(md.Control.Node), ["missing"]),
			MissingAttrDiff ma => (Text(ma.Control.Attribute), ["missing"]),
			UnexpectedNodeDiff ud => (["missing"], Text(ud.Test.Node)),
			UnexpectedAttrDiff ua => (["missing"], Text(ua.Test.Attribute)),
			_ => ([], []),
		};

		var lines = new List<string> { label };
		Add(lines, "  expected: ", expected);
		Add(lines, "  actual:   ", actual);
		return lines;
	}

	private static void Add(List<string> lines, string prefix, string[] text)
	{
		for (var i = 0; i < text.Length; i++)
			lines.Add(i > 0 && text[i].Length == 0 ? string.Empty : (i == 0 ? prefix : new string(' ', prefix.Length)) + text[i]);
	}

	private static string Kind(ComparisonSource source) => source.Node.NodeType.ToString().ToLowerInvariant();

	private static string[] Text(INode node)
	{
		using var writer = new StringWriter { NewLine = "\n" };
		node.ToHtml(writer, new WhitespaceKeepingFormatter { NewLine = "\n", Indentation = "\t" });
		return Cap(writer.ToString().Trim('\n'));
	}

	private static string[] Text(IAttr attribute) => Cap($"{attribute.Name}=\"{attribute.Value}\"");

	private static string[] Cap(string text)
	{
		var lines = text.Split('\n');
		return lines.Length <= MaxNodeLines ? lines : [.. lines.Take(MaxNodeLines - 1), $"… ({lines.Length - MaxNodeLines + 1} more lines)"];
	}
}

/// <summary>HTML stored pretty-printed and compared structurally.</summary>
internal sealed class HtmlSnapshotFormat : SnapshotFormat
{
	public override string Name => "HTML";

	public override string Extension => "html";

	// Both sides are already pretty-printed by the assertion, so this compares structure only: a passing comparison
	// never rewrites the verified file, even when whitespace differs.
	public override SnapshotComparison Compare(string verified, string actual) => HtmlSnapshot.Compare(verified, actual);
}
