---
name: nullean-snapshots-awesomeassertions
description: >-
  Use when writing a snapshot assertion on a string in a .NET test with AwesomeAssertions, as a verified file or as an
  inline literal, or building a format-specific snapshot assertion. Nullean.Snapshots.AwesomeAssertions adds
  `MatchSnapshot` and `MatchInlineSnapshot` to `Should()`.
---

# Nullean.Snapshots.AwesomeAssertions

Text snapshot assertions for AwesomeAssertions, and the building blocks the JSON, YAML and HTML packages use. For
structured output prefer `Nullean.Snapshots.Json` or `Nullean.Snapshots.Yaml`, which compare as trees, not lines.

## The calls you need

`using AwesomeAssertions;` and `using Nullean.Snapshots;` (the extensions live in `Nullean.Snapshots`).

```csharp
report.Should().MatchSnapshot();                    // .verified.txt, compared line by line
header.Should().MatchSnapshot(name: "header");      // a second snapshot in the same test
log.Should().MatchSnapshot(options: new SnapshotOptions().Scrub(Scrubbers.Guids));
```

Inline: the expected value is a literal in the test. Leave it out for a new one; `snap accept` writes it in.

```csharp
greeting.Should().MatchInlineSnapshot("Hello, Kibana!");
table.Should().MatchInlineSnapshot("""
    2 x beats
    1 x kibana
    """);
```

A format package's assertion, reusing the store and the verbatim failure message:

```csharp
value.Should().MatchSnapshot(SnapshotFormats.Text, name: "raw");
SnapshotAssertions.FailUnless(assertions.CurrentAssertionChain, ok, message);
```

## Gotchas

- The first run of a new snapshot fails by design. Read the received file, then accept with `snap accept <key>` or
  `SNAPSHOT_ACCEPT=1` (never in CI).
- A `null` subject fails the assertion; nothing is written.
- Two `MatchSnapshot` calls in one test need distinct `name`s, or the second throws `InvalidOperationException`.
  `MatchInlineSnapshot` needs none: its key ends in the call's line, `.L<line>`.
- Only a literal argument can be accepted: a constant, variable or interpolated string compares but its accept is
  refused. A data-driven test whose cases produce different values cannot accept one literal; use `MatchSnapshot`.
- Never put snapshot text in a format string: pass it to `FailUnless`, which shows braces as is.
- Failures join an `AssertionScope` like any other assertion.

Docs: https://snapshots.nullean.net/formats/inline
