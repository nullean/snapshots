# Inline snapshots

```csharp
[Test]
public void Greets() =>
    Greeter.Greet("Kibana").Should().MatchInlineSnapshot("Hello, Kibana!");

[Test]
public void RendersTheBasket() =>
    Basket.Render().Should().MatchInlineSnapshot("""
        2 × beats
        1 × kibana
        """);
```

An inline snapshot keeps the expected value in the test, as a string literal, instead of in a verified file. It suits
values you want to read next to the assertion: a message, a small object, a few lines of output. Anything big or
binary is better as a file.

Every format has one: `MatchInlineSnapshot` (text), `MatchInlineJsonSnapshot`, `MatchInlineYamlSnapshot` and
`MatchInlineHtmlSnapshot`. Each compares as its file-backed twin does (JSON and YAML as trees, HTML as a DOM) and takes
the same options, and the literal is compared after the usual normalisation: line endings, and one trailing newline.

```csharp
json.Should().MatchInlineJsonSnapshot("""
    { "id": 2, "name": "Kibana" }
    """);
```

The `expected` parameter of the JSON, YAML and HTML assertions (and of `BeJson`, `BeYaml`, `BeHtml`) carries
`[StringSyntax]`, so editors that understand it highlight the literal as that language.

## Writing the value

Leave the argument out and the assertion fails, as a new snapshot does:

```csharp
report.Should().MatchInlineSnapshot();
```

```text
Text inline snapshot is new: MatchInlineSnapshot() at tests/App.Tests/ReportTests.cs:12 has no expected value
Received: .artifacts/.snapshots/received/App.Tests/ReportTests/Renders.L12.received.inline
Accept:   snap accept App.Tests/ReportTests/Renders.L12   (or: dnx Nullean.Snapshots.Cli accept App.Tests/ReportTests/Renders.L12)
```

`snap accept` then writes the value into the source: a regular literal for one line, a raw `"""` literal for several,
indented one level past the line of the call and with more quotes when the value holds `"""`. The same happens when a
literal is wrong. This is a plain text edit made by `snap` itself, so updating expected strings does not need an agent
or an editor. `snap review`, `snap diff` and `snap status` treat an inline snapshot like any other, with the test
file and line in place of a verified file.

```sh
SNAPSHOT_ACCEPT=1 dotnet test --project tests/App.Tests
```

does the same without the CLI, when the test process ends. It never runs in CI, like every accept.

## What can be rewritten

`snap` edits the text of the literal, so the argument has to be one:

- a regular, verbatim (`@"…"`) or raw (`"""…"""`) string literal, or `null`, or left out;
- not an interpolated string, a constant, a concatenation or a variable. Those compare, but accepting one is refused
  with the reason, and the snapshot stays pending.

An accept is also refused, changing nothing, when:

- **the test file changed since the test ran.** The received record holds a hash of the file, because a literal that
  grows moves the lines below it. Run the tests again.
- **test cases disagree.** A data-driven test runs its one literal once per case, and a test in an abstract base class
  marked `[InheritsTests]` runs it once per derived class; if two of them produce different values there is nothing
  to write, because the literal is in the base. Use `MatchSnapshot`, whose files are per case and per runtime class.
  When every case agrees, accept writes the literal as usual.

```csharp
public abstract class DirectiveTests
{
    [Test]
    public void Renders() => Render(Name).Should().MatchInlineSnapshot("..."); // one literal, shared by the derived classes
}

[InheritsTests]
public sealed class NoteTests : DirectiveTests;
```

Each derived class records its own received value, so a test that fails in one of them names that class; only the accept is refused
when the values differ.
- **the file is outside the test project** (a linked file). The library edits no source file outside the project, and
  `snap` none outside `--root`.

Several snapshots in one file are written together, so accepting them one after another is safe.

## Where the received value goes

A failing inline snapshot writes a small JSON file next to the other received files,
`{key}.L{line}.received.inline`, naming the test file, the line, the literal as it was and the value to write. Nothing
is written into your source until you accept. The key ends in the line of the call, so two calls in one test need no
name; moving a call to another line renames its key, which only matters while a record is pending.
