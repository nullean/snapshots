# Formats

Text formats store their snapshots as UTF-8 without a byte order mark, with LF line endings and one trailing newline;
images are stored exactly as given. Each compares in its own way:

| Format | Stored as | Compared |
|---|---|---|
| [Text](text.md) | the text | line by line, shown as unified hunks |
| [Inline](inline.md) | a string literal in the test | as its format: text, HTML, JSON or YAML |
| [HTML](html.md) | pretty-printed HTML | as a DOM with AngleSharp.Diffing, plus a line diff |
| [JSON](json.md) | pretty-printed JSON | as a tree: keys in any order, numbers by value |
| [YAML](yaml.md) | the YAML as written | as a tree with the YAML 1.2 core schema |
| [Images](images.md) | the PNG as given | by pixels, perceptually, with anti-aliasing left out |
| [Browser journeys](browser.md) | a transcript of a journey's reads | section by section, accessibility trees as trees |
| [HTTP](http.md) | a transcript of each response: status, chosen headers, body | section by section, JSON bodies as trees |

Any of the text formats can also live in the test itself, as an [inline snapshot](inline.md).

Structural formats never rewrite a verified file that still matches: reformatting it by hand, a whitespace change in
the output, or an image re-encoded with the same pixels does not fail a test.

## Scrubbing volatile values

Values that change every run, such as GUIDs and timestamps, can be replaced before comparing:

```csharp
json.Should().MatchJsonSnapshot(options: new SnapshotOptions().Scrub(Scrubbers.Guids).Scrub(Scrubbers.DateTimes));
```

`Scrubbers.Guids` and `Scrubbers.DateTimes` number what they replace (`Guid_1`, `Guid_2`, …), the same value always
getting the same number, so references between values survive. A scrubber is any `Func<string, string>`, or a regex
and its replacement. To scrub every snapshot, register them once, for instance from a module initializer:

```csharp
[ModuleInitializer]
internal static void Snapshots() => SnapshotDefaults.AddScrubber(Scrubbers.Guids);
```

`Scrubbers.Path(root, replacement)` replaces a folder where it starts a path and writes the rest of the path with `/`, so a Windows run
gives the same text (`C:\work\repo\docs\a.md` and its JSON-escaped form both become `{repo}/docs/a.md`); `Scrubbers.TempDirectory()` is the
system temp folder as `{temp}`, including the resolved form where the temp folder is a link (`/private/var` on macOS):

```csharp
new SnapshotOptions().Scrub(Scrubbers.Path(repoRoot, "{repo}")).Scrub(Scrubbers.TempDirectory());
```

A drive letter from an in-memory file system (`C:\docs\docset.yml` against `/docs/docset.yml`) is not a root of this machine: scrub it with
`Scrubbers.Path("C:/docs", "/docs")`, or a regex.

`Scrubbers.Values(values, name)` is for values the test discovers at run time, such as the repositories it cloned. Each one becomes
`name_1`, `name_2`, … in order of first appearance, the same value always getting the same number:

```csharp
new SnapshotOptions().Scrub(Scrubbers.Values(repositories.Select(r => r.Name), "Repo"));
```

Matching is case-sensitive and by whole word (`abc` is not found in `abcd`), and the longer of two overlapping values wins. The set is read when
you call `Values`.

## Forbidding values

A scrubber hides a value; `Forbid` makes sure it never shows up at all, such as a secret or the name of a private repository:

```csharp
new SnapshotOptions().Forbid(secrets);   // one call
SnapshotDefaults.Forbid(secrets);         // every snapshot, from a module initializer
```

A snapshot fails when the scrubbed value, or its verified file, contains one of them (ordinal match). The message gives the
line numbers and never the values, and no received file is written, so a forbidden value does not reach the disk. Accepting does not get
around it, and `IgnoreDefaultScrubbers` does not skip the default list.

Nothing is scrubbed unless you ask for it.
