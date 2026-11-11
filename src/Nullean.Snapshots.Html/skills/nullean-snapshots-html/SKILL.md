---
name: nullean-snapshots-html
description: >-
  Use when a C# test needs to assert on rendered HTML: snapshot a page or fragment, compare it with expected markup,
  or check that a fragment is or is not present. Nullean.Snapshots.Html adds AwesomeAssertions extensions on strings
  that pretty-print with AngleSharp and compare structurally.
---

# Nullean.Snapshots.Html

Assert on HTML strings without caring about whitespace or attribute order. It parses markup only, with no scripts
or browser; for plain text use `MatchSnapshot` from `Nullean.Snapshots.AwesomeAssertions`.

## The calls you need

`using Nullean.Snapshots.Html;` then, on a `string`:

```csharp
html.Should().MatchHtmlSnapshot();                       // .snapshots/{dir}/{Class}/{Method}.verified.html
html.Should().MatchHtmlSnapshot(name: "after-save");     // a second snapshot in the same test
```

Narrow what is printed (select, remove elements, remove attributes, transforms, in that order):

```csharp
var options = new HtmlSnapshotOptions()
	.Select("main#content")
	.Remove("script")
	.RemoveAttributes("hx-*", "nonce")
	.RemoveAttributesFrom("a", "data-*");
html.Should().MatchHtmlSnapshot(options);
```

Compare with markup in the test:

```csharp
html.Should().BeHtml("<p class=\"lead\">Welcome</p>");
html.Should().ContainHtml("<strong>Welcome</strong>");
html.Should().NotContainHtml("<script>");
html.Should().MatchInlineHtmlSnapshot("""<p>Hi</p>""");
```

## Gotchas

- `Select` prints the matched element's children, not the element. A selector that matches nothing throws
  `InvalidOperationException`, it does not fail the assertion.
- Two `MatchHtmlSnapshot` calls in one test need distinct `name` values, or the second throws
  `InvalidOperationException`.
- `HtmlSnapshotOptions` change what is stored. Adding a `Remove` later makes existing verified files fail until
  re-accepted.
- `BeHtml` and `MatchHtmlSnapshot` are structural. `ContainHtml` is not: it compares pretty-printed text with tabs
  removed, so attribute order and text must match exactly.
- A passing match never rewrites the verified file, so its old formatting stays.
- Scrubbers in `SnapshotOptions` run on the pretty-printed HTML, after `HtmlSnapshotOptions`.
- `MatchInlineHtmlSnapshot()` with no argument always fails; accept it to write the literal into the source.

Docs: https://snapshots.nullean.net/formats/html
