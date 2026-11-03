# Formats

Every format stores its snapshots as UTF-8 without a byte order mark, with LF line endings and one trailing newline,
and compares in its own way:

| Format | Stored as | Compared |
|---|---|---|
| [Text](text.md) | the text | line by line, shown as unified hunks |
| [HTML](html.md) | pretty-printed HTML | as a DOM with AngleSharp.Diffing, plus a line diff |
| [JSON](json.md) | pretty-printed JSON | as a tree: keys in any order, numbers by value |
| [YAML](yaml.md) | the YAML as written | as a tree with the YAML 1.2 core schema |

Structural formats never rewrite a verified file that still matches: reformatting it by hand, or a whitespace change
in the output, does not fail a test.

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

Nothing is scrubbed unless you ask for it.
