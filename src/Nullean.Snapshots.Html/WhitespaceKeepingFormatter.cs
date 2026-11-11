using AngleSharp.Dom;
using AngleSharp.Html;

namespace Nullean.Snapshots.Html;

/// <summary>
/// AngleSharp's pretty printer, except that nothing inside <c>&lt;pre&gt;</c>, <c>&lt;textarea&gt;</c> or <c>&lt;listing&gt;</c> is
/// changed: whitespace is significant there, and the printer would turn every newline into a space and indent before a child
/// element, so a code block's line structure was lost from the snapshot and a change that only moved newlines in it did not fail.
/// </summary>
internal sealed class WhitespaceKeepingFormatter : PrettyMarkupFormatter
{
	private static bool IsPreformatted(IElement element) => Preformatted.Is(element);

	private static bool InsidePreformatted(INode node) => Preformatted.Inside(node);

	/// <inheritdoc />
	public override string Text(ICharacterData text)
	{
		// The plain HTML serializer escapes the text and, as the specification asks, writes an extra newline first when the content starts with one
		// (a parser drops one newline after the start tag of these elements).
		return InsidePreformatted(text) ? HtmlMarkupFormatter.Instance.Text(text) : base.Text(text);
	}

	/// <inheritdoc />
	public override string OpenTag(IElement element, bool selfClosing) =>
		InsidePreformatted(element) ? HtmlMarkupFormatter.Instance.OpenTag(element, selfClosing) : base.OpenTag(element, selfClosing);

	/// <inheritdoc />
	public override string CloseTag(IElement element, bool selfClosing)
	{
		if (InsidePreformatted(element))
			return HtmlMarkupFormatter.Instance.CloseTag(element, selfClosing);
		var tag = base.CloseTag(element, selfClosing);
		if (!IsPreformatted(element))
			return tag;
		// The printer puts the closing tag of a block on a line of its own, which would add a newline to the end of the content.
		var start = tag.IndexOf('<', StringComparison.Ordinal);
		return start > 0 ? tag[start..] : tag;
	}
}

/// <summary>The elements whose whitespace is content: <c>pre</c>, <c>textarea</c> and <c>listing</c>.</summary>
internal static class Preformatted
{
	/// <summary>Whether <paramref name="element"/> is one of them.</summary>
	public static bool Is(IElement element) => element.LocalName is "pre" or "textarea" or "listing";

	/// <summary>Whether <paramref name="node"/> is anywhere inside one of them, however deeply.</summary>
	public static bool Inside(INode node)
	{
		for (var parent = node.ParentElement; parent is not null; parent = parent.ParentElement)
		{
			if (Is(parent))
				return true;
		}

		return false;
	}
}
