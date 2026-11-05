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
