using AwesomeAssertions;
using CsCheck;
using Nullean.Snapshots.Tests;

namespace Nullean.Snapshots.Html.Tests;

public class HtmlSnapshotTests
{
	/// <summary>Random HTML from a small grammar: nested elements, attributes, and text with characters that need escaping.</summary>
	private static readonly Gen<string> Html = Gen.Recursive<string>((depth, inner) =>
	{
		var text = Gen.String[Gen.Char["ab <>&\"'{}"], 0, 6].Select(System.Net.WebUtility.HtmlEncode);
		if (depth >= 3)
			return text;
		var attribute = Gen.Select(Gen.OneOf("class", "id", "hx-get", "data-x"), Gen.String[Gen.Char["xyz"], 0, 3]).Select((n, v) => $" {n}=\"{v}\"");
		var element = Gen.Select(Gen.OneOf("p", "div", "span", "b", "ul", "li", "a"), attribute.Array[0, 2], inner.Array[0, 3]).Select(
			(tag, attrs, children) => $"<{tag}{string.Concat(attrs)}>{string.Concat(children)}</{tag}>"
		);
		return Gen.Frequency((1, text), (3, element));
	});

	[Test]
	public void PrettyIsIdempotent() =>
		Html.Sample(html =>
		{
			var once = HtmlSnapshot.Pretty(html);
			return HtmlSnapshot.Pretty(once) == once;
		});

	// The whitespace the printer adds must be invisible to the structural comparison, or a snapshot would not match
	// the HTML it was printed from.
	[Test]
	public void PrettyPrintingDoesNotChangeStructure() => Html.Sample(html => HtmlSnapshot.Compare(html, HtmlSnapshot.Pretty(html)).IsMatch);

	[Test]
	public void PrettyIndentsWithTabsAndEndsInOneNewline() =>
		HtmlSnapshot.Pretty("<div><p>hi <b>there</b></p></div>").Should().Be("<div>\n\t<p>hi\n\t\t<b>there</b>\n\t</p>\n</div>\n");

	[Test]
	public void TopLevelTextIsKeptAndWhitespaceDropped() =>
		HtmlSnapshot.Pretty("\n  <p>a</p>\n  loose text \n<p>b</p>").Should().Be("<p>a</p>\nloose text\n<p>b</p>\n");

	// Regression, found by PrettyIsIdempotent: top-level text was written unescaped, so "&lt;a" came back as a tag.
	[Test]
	public void TopLevelTextIsEscaped() => HtmlSnapshot.Pretty("&amp;&lt;a}}").Should().Be("&amp;&lt;a}}\n");

	[Test]
	public void SelectPrintsTheMatchedElementsContent() =>
		HtmlSnapshot.Pretty("<header>x</header><main id=c><p>kept</p></main>", new HtmlSnapshotOptions().Select("main#c")).Should().Be("<p>kept</p>\n");

	[Test]
	public void SelectorThatMatchesNothingThrows()
	{
		var act = () => HtmlSnapshot.Pretty("<p>x</p>", new HtmlSnapshotOptions().Select("main"));
		act.Should().Throw<InvalidOperationException>().WithMessage("The HTML has no element matching 'main'.");
	}

	[Test]
	public void RemoveDropsElements() =>
		HtmlSnapshot.Pretty("<p>a<script>x()</script></p><nav>n</nav>", new HtmlSnapshotOptions().Remove("script").Remove("nav")).Should().Be("<p>a</p>\n");

	[Test]
	public void RemoveAttributesTakesWildcards() =>
		HtmlSnapshot
			.Pretty(
				"<a href=x hx-get=y hx-swap=z preload data-hx=keep>l</a><div hx-get=d></div>",
				new HtmlSnapshotOptions().RemoveAttributes("hx-*", "preload")
			)
			.Should()
			.Be("<a href=\"x\" data-hx=\"keep\">l</a>\n<div></div>\n");

	[Test]
	public void RemoveAttributesFromOnlyTouchesMatchingElements() =>
		HtmlSnapshot
			.Pretty("<a hx-get=y>l</a><div hx-get=d></div>", new HtmlSnapshotOptions().RemoveAttributesFrom("a", "hx-*"))
			.Should()
			.Be("<a>l</a>\n<div hx-get=\"d\"></div>\n");

	// docs-builder's harness drops a leading H1; that stays in docs-builder, as a transform like this one.
	[Test]
	public void TransformRunsBeforePrinting() =>
		HtmlSnapshot
			.Pretty("<h1>Test Document</h1><p>body</p>", new HtmlSnapshotOptions().Transform(root =>
			{
				if (root.FirstElementChild is { TagName: "H1" } h1)
					h1.Remove();
			}))
			.Should()
			.Be("<p>body</p>\n");

	[Test]
	[Arguments("hx-*", "hx-get", true)]
	[Arguments("hx-*", "HX-GET", true)]
	[Arguments("hx-*", "data-hx", false)]
	[Arguments("*-x", "data-x", true)]
	[Arguments("*-x", "data-y", false)]
	[Arguments("d*a*x", "data-x", true)]
	[Arguments("d*q*x", "data-x", false)]
	[Arguments("ab*ba", "aba", false)]
	[Arguments("preload", "preload", true)]
	[Arguments("preload", "preloads", false)]
	public void AttributePatterns(string pattern, string name, bool expected) => HtmlSnapshot.Matches(pattern, name).Should().Be(expected);

	[Test]
	public void StructuralCompareIgnoresAttributeOrderAndWhitespace() =>
		HtmlSnapshot.Compare("<p class=\"a\" id=\"b\">hi</p>\n", "<p id=\"b\"   class=\"a\">\n\thi\n</p>\n").IsMatch.Should().BeTrue();

	[Test]
	public void DifferencesAreLabelledAndComeWithALineDiff()
	{
		var comparison = HtmlSnapshot.Compare(HtmlSnapshot.Pretty("<p class=\"a\">hi <b>x</b></p>"), HtmlSnapshot.Pretty("<p class=\"b\">hi <i>x</i></p>"));
		comparison.IsMatch.Should().BeFalse();
		comparison
			.Differences
			.Select(d => d[0])
			.Should()
			.Equal(
				"The values of the attribute at p(0)[class] are different.",
				"The element at p(0) > b(1) is missing.",
				"The element at p(0) > i(1) was not expected."
			);
		comparison
			.Differences[0]
			.Should()
			.Equal("The values of the attribute at p(0)[class] are different.", "  expected: class=\"a\"", "  actual:   class=\"b\"");
		comparison.LineDiff.Should().NotBeNullOrEmpty();
	}

	[Test]
	public void LongNodesAreCut()
	{
		var big = "<ul>" + string.Concat(Enumerable.Range(0, 30).Select(i => $"<li>{i}</li>")) + "</ul>";
		var comparison = HtmlSnapshot.Compare(HtmlSnapshot.Pretty("<p>x</p>"), HtmlSnapshot.Pretty("<p>x</p>" + big));
		var unexpected = comparison.Differences.Single();
		unexpected.Should().HaveCount(1 + 1 + HtmlSnapshot.MaxNodeLines);
		unexpected[^1].Should().EndWith("more lines)");
	}
}
