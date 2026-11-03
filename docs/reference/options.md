# Paths and options

## Paths

JSON and YAML differences are reported by a JSONPath-like path:

| Path | Means |
|---|---|
| `$` | the root |
| `$.name` | the `name` key |
| `$["key with.dots"]` | a key that is not a plain identifier |
| `$.items[2]` | the third item |
| `$doc[1].name` | in the second document of a YAML stream |
| `$[1].v (actual $[2].v)` | the verified path, and where the item sits in the actual tree |

## Ignore patterns

`TreeDiffOptions.IgnorePaths` takes paths with wildcards:

| Pattern | Ignores |
|---|---|
| `$.meta.generatedAt` | that key |
| `$.items[*].etag` | `etag` in every item |
| `$.config.*` | every key under `config` |
| `$..id` | every `id` at any depth |

## TreeDiffOptions

| Option | Default | Effect |
|---|---|---|
| `MapOrder` | `Insensitive` | `Significant` reports a different key order |
| `StrictNumbers` | `false` | compare numbers by kind and text, so `1` and `1.0` differ |
| `NullEqualsMissing` | `false` | a key with `null` equals the key being absent |
| `IgnorePaths` | none | see above |
| `CellCap` | 4,000,000 | arrays whose lengths multiply to more are aligned by position, not by LCS |

## SnapshotOptions

Per call: `Scrub(Func<string, string>)`, `Scrub(Regex, string)`, `IgnoreDefaultScrubbers` and `IgnoreClassArguments`. See
[scrubbing](../formats/index.md#scrubbing-volatile-values) and [class arguments](#class-arguments).

## Class arguments

A test class that receives a fixture through its constructor gets an arguments hash in every file name, because the
constructor arguments are part of the test's identity. When those arguments are dependencies and not data, leave them
out:

```csharp
html.Should().MatchHtmlSnapshot(options: new SnapshotOptions { IgnoreClassArguments = true });

// or once for the whole process, from a module initializer
SnapshotDefaults.IgnoreClassArguments = true;
```

The file is then named `{Method}.verified.html`. Method arguments still add their hash, and a per-call value of `false`
turns the default off for one call.

Use this only when the class arguments do not change what is snapshotted. Two instances of one class with different
data would claim the same file, and the second fails with `Two snapshot assertions resolve to ...`.
