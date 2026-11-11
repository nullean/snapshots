# HTML

```csharp
using Nullean.Snapshots.Html;

page.Should().MatchHtmlSnapshot();
page.Should().BeHtml("<p class=\"lead\">Welcome</p>");
page.Should().ContainHtml("<strong>Welcome</strong>");
page.Should().NotContainHtml("<script>");
```

HTML is parsed with AngleSharp, pretty-printed (one element per line, tab indented) and compared structurally with
AngleSharp.Diffing: attribute order and whitespace do not matter. A failure names each difference and shows a line
diff of the pretty-printed HTML:

```text
HTML did not match snapshot: tests/Docs.Tests/.snapshots/Rendering/TableTests/Renders.verified.html
2 difference(s):
The text in table(0) > tbody(1) > tr(1) > td(1) > #text(0) is different.
  expected: {kibana}
  actual:   {Kibana}
The attribute at table(0) > tbody(1) > tr(1) > td(1)[class] was not expected.
  expected: missing
  actual:   class="x"
Line diff:
@@ line 1 @@
 <table>
 	<tbody>
 		<tr>
-			<td>{kibana}</td>
+			<td class="x">{Kibana}</td>
```

## Code blocks keep their whitespace

Whitespace is significant inside `<pre>`, `<textarea>` and `<listing>`, so the snapshot keeps it exactly, at any depth: a code block's lines,
blank lines and indentation, with the markup of a highlighted block (`<pre><code><span>…`) printed as it is, on the lines it has. A change that only
moves a newline or a space there fails the comparison, and the message shows the lines. Everywhere else whitespace still does not matter.

## Narrowing what is compared

`HtmlSnapshotOptions` select, remove and transform before printing, applied to both sides:

```csharp
var html = new HtmlSnapshotOptions()
	.Select("main#content")                 // print this element's content instead of the body
	.Remove("script")                       // drop matching elements
	.RemoveAttributes("hx-*", "preload")    // drop attributes everywhere; * is a wildcard
	.RemoveAttributesFrom("a", "data-*")    // or only from matching elements
	.Transform(root => root.FirstElementChild?.Remove());

page.Should().MatchHtmlSnapshot(html);
```

`ContainHtml` ignores indentation, so a fragment matches wherever it is nested.

For a short value there is `MatchInlineHtmlSnapshot`, which keeps the expected value in the test as a literal: see [inline snapshots](inline.md).
